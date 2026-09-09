using Silk.NET.OpenGL;

namespace RecompOne.Runtime.Hle;

internal static class GlShaders
{
    public const string FullscreenVs = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        out vec2 vUv;
        void main() {
            vUv = aPos * 0.5 + 0.5;
            gl_Position = vec4(aPos, 0.0, 1.0);
        }
        """;

    public const string PresentFs = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uVram;
        uniform sampler2D uAo;
        uniform vec2 uOrigin;
        uniform vec2 uSize;
        uniform vec2 uTexSize;
        // 0 leaves the present bit-identical to what it has always been: the
        // multiply is skipped, not multiplied by one, so a build with the pass
        // switched off cannot round a colour by a least significant bit.
        uniform float uAoOn;
        out vec4 oColor;
        void main() {
            vec2 t = (uOrigin + vUv * uSize) / uTexSize;
            vec3 c = texture(uVram, t).rgb;
            // The occlusion texture is rendered at exactly this framebuffer's
            // size, so it is indexed by the present's own uv and needs no
            // geometry of its own.
            if (uAoOn > 0.5) c *= texture(uAo, vUv).r;
            oColor = vec4(c, 1.0);
        }
        """;

    public const string Present24Fs = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uVram;
        uniform vec2 uOrigin;
        uniform vec2 uSize;
        uniform int uScale;
        out vec4 oColor;

        int u5(float f) { return int(floor(f * 31.0 + 0.5)); }
        int texel16(int lin) {
            vec4 p = texelFetch(uVram, ivec2((lin & 1023) * uScale, ((lin >> 10) & 511) * uScale), 0);
            return u5(p.r) | (u5(p.g) << 5) | (u5(p.b) << 10) | (int(ceil(p.a)) << 15);
        }
        int byteAt(int b) {
            int t = texel16(b >> 1);
            return (b & 1) == 0 ? (t & 0xff) : ((t >> 8) & 0xff);
        }
        void main() {
            int px = int(floor(vUv.x * uSize.x));
            int py = int(floor(vUv.y * uSize.y));
            int ty = int(uOrigin.y) + py;
            int base = (ty * 1024 + int(uOrigin.x)) * 2 + px * 3;
            oColor = vec4(float(byteAt(base)) / 255.0, float(byteAt(base + 1)) / 255.0,
                          float(byteAt(base + 2)) / 255.0, 1.0);
        }
        """;

    /// <summary>
    /// Ambient occlusion, first pass: the finished frame's depth attachment in,
    /// one occlusion factor per output pixel out.
    ///
    /// The depth texel holds the GTE's own view depth over 65536, so undoing the
    /// game's projection gets a view position straight back out of it — the divide
    /// was <c>screen = centre + IR * H / z</c>, so <c>view.xy = (screen - centre) *
    /// z / H</c> with H and the centre published from <c>Gte.Rtp</c> rather than
    /// assumed. That is what makes this the game's geometry rather than a
    /// plausible-looking depth trick: the reconstructed normal is the surface's
    /// own, to whatever precision the recovered SZ has.
    ///
    /// A texel at the far plane is one nothing wrote: 2D, or a triangle the vertex
    /// map missed. It is neither shaded nor allowed to occlude, so the HUD, the
    /// menus and the death fade come through untouched and a hole in the depth
    /// buffer costs occlusion rather than inventing it.
    /// </summary>
    public const string AoFs = """
        #version 330 core
        in vec2 vUv;
        out vec4 oColor;

        uniform sampler2D uDepth;
        // The display area inside the render target, and the target's size, both
        // in the game's own 1x pixels -- the same three numbers the present shader
        // is given, so the two passes address the same rectangle.
        uniform vec2  uOrigin;
        uniform vec2  uSize;
        uniform vec2  uTexSize;
        // One depth texel as a step in this pass's own uv, which is not 1/uSize:
        // the target is rendered at the render scale and the fine structure of the
        // depth buffer is at that scale, not the game's.
        uniform vec2  uTexel;
        // The GTE's projection: distance, and the screen centre the divide is
        // offset by, expressed in this pass's uv so no pixel arithmetic has to be
        // repeated here.
        uniform float uProjH;
        uniform vec2  uCentre;
        uniform float uRadius;
        uniform float uStrength;
        uniform float uBias;
        uniform float uMaxDepth;
        uniform int   uSamples;

        const float FAR = 65536.0;
        const float GOLDEN = 2.39996323;

        float depthAt(vec2 uv) {
            return texture(uDepth, (uOrigin + clamp(uv, 0.0, 1.0) * uSize) / uTexSize).r;
        }

        // The view position of the point this pass's uv names, given its depth.
        // Screen X and Y are in the game's own pixels measured from the projection
        // centre, which is what uCentre and uSize turn a uv into.
        vec3 viewAt(vec2 uv, float d) {
            float z = d * FAR;
            return vec3((uv - uCentre) * uSize * (z / uProjH), z);
        }

        // The neighbour on each axis that is nearer in depth, so a pixel on a
        // silhouette takes its normal from the surface it belongs to instead of
        // straddling the edge and coming out facing the camera.
        vec3 nearer(vec3 p, vec3 a, vec3 b) {
            return abs(a.z - p.z) < abs(b.z - p.z) ? a - p : p - b;
        }

        void main() {
            // Red is the occlusion factor the present multiplies by. Green says
            // whether there was a surface here at all -- it is not used to draw
            // anything, it is what lets the census tell "the mask refused this
            // pixel" from "the pass looked and found nothing to shade it with",
            // which are the two ways a blank patch happens and are not the same
            // bug. See KF2_AO_PROBE=2.
            float d = depthAt(vUv);
            // Nothing wrote here (2D, or a triangle with no recovered depth), or
            // the fog has the picture: unoccluded, and the present multiplies by 1.
            if (d >= 1.0 || d <= 0.0) { oColor = vec4(1.0, 0.0, 0.0, 1.0); return; }

            vec3 p = viewAt(vUv, d);
            if (p.z > uMaxDepth) { oColor = vec4(1.0, 0.0, 0.0, 1.0); return; }

            vec2 sx = vec2(uTexel.x, 0.0), sy = vec2(0.0, uTexel.y);
            float dl = depthAt(vUv - sx), dr = depthAt(vUv + sx);
            float du = depthAt(vUv - sy), dd = depthAt(vUv + sy);
            // A neighbour with no depth is not a position; fall back to this
            // pixel's own so the difference is taken against the other side.
            vec3 l = dl > 0.0 && dl < 1.0 ? viewAt(vUv - sx, dl) : p;
            vec3 r = dr > 0.0 && dr < 1.0 ? viewAt(vUv + sx, dr) : p;
            vec3 u = du > 0.0 && du < 1.0 ? viewAt(vUv - sy, du) : p;
            vec3 b = dd > 0.0 && dd < 1.0 ? viewAt(vUv + sy, dd) : p;

            vec3 n = cross(nearer(p, r, l), nearer(p, b, u));
            if (dot(n, n) < 1e-12) { oColor = vec4(1.0, 1.0, 0.0, 1.0); return; }
            n = normalize(n);
            // The camera is at the origin looking down +Z, so a surface facing it
            // has a negative dot with its own position.
            if (dot(n, p) > 0.0) n = -n;

            // The world radius as it projects at this depth, in uv. Clamped at the
            // near end because a sphere a hand's width across fills the screen when
            // the camera is inside it, and at the far end to a texel so the kernel
            // never collapses onto the pixel it is shading.
            float rPix = clamp(uRadius * uProjH / p.z, 1.5, 96.0);
            vec2 rUv = rPix / uSize;

            // A 4x4 interleaved rotation rather than a hash: the blur below is a
            // 4x4 box, so a pattern with that period cancels exactly and leaves no
            // residual grain, where noise leaves noise.
            ivec2 px = ivec2(gl_FragCoord.xy) & 3;
            float a0 = float((px.y << 2) | px.x) * (6.28318531 / 16.0);

            float occ = 0.0;
            for (int i = 0; i < uSamples; i++) {
                float t = (float(i) + 0.5) / float(uSamples);
                float ang = a0 + float(i) * GOLDEN;
                vec2 off = vec2(cos(ang), sin(ang)) * sqrt(t) * rUv;

                float sd = depthAt(vUv + off);
                // The far plane is the absence of a surface, not a surface a long
                // way off: it must not occlude, or every silhouette against the
                // HUD would draw a dark halo.
                if (sd >= 1.0 || sd <= 0.0) continue;

                vec3 v = viewAt(vUv + off, sd) - p;
                float len = length(v);
                if (len < 1e-4) continue;
                // Falls off past the radius instead of stopping at it, so a wall
                // sliding out of range dims rather than switching off.
                float range = uRadius / max(uRadius, len);
                occ += max(0.0, dot(v / len, n) - uBias) * range;
            }

            float ao = 1.0 - uStrength * (occ / float(uSamples));
            oColor = vec4(clamp(ao, 0.0, 1.0), 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// Ambient occlusion, second pass: the 4x4 box that cancels the first pass's
    /// 4x4 rotation exactly, weighted by depth so it does not carry a wall's
    /// occlusion across a silhouette onto whatever is behind it.
    /// </summary>
    public const string AoBlurFs = """
        #version 330 core
        in vec2 vUv;
        out vec4 oColor;

        uniform sampler2D uAo;
        uniform sampler2D uDepth;
        uniform vec2  uOrigin;
        uniform vec2  uSize;
        uniform vec2  uTexSize;
        uniform vec2  uTexel;
        // How far apart two depths may be, as a fraction of the nearer one, and
        // still be treated as the same surface. Relative rather than absolute
        // because the recovered depth is a view depth: a step that is a crease at
        // arm's length is a rounding error across a room.
        uniform float uEdge;

        float depthAt(vec2 uv) {
            return texture(uDepth, (uOrigin + clamp(uv, 0.0, 1.0) * uSize) / uTexSize).r;
        }

        void main() {
            float d = depthAt(vUv);
            if (d >= 1.0 || d <= 0.0) { oColor = vec4(1.0, 0.0, 0.0, 1.0); return; }

            float sum = 0.0, wsum = 0.0;
            for (int y = -2; y <= 1; y++) {
                for (int x = -2; x <= 1; x++) {
                    vec2 uv = vUv + vec2(float(x), float(y)) * uTexel;
                    float sd = depthAt(uv);
                    if (sd >= 1.0 || sd <= 0.0) continue;
                    if (abs(sd - d) > uEdge * d) continue;
                    sum += texture(uAo, uv).r;
                    wsum += 1.0;
                }
            }
            oColor = vec4(wsum > 0.0 ? sum / wsum : texture(uAo, vUv).r, 1.0, 0.0, 1.0);
        }
        """;

    public const string PrimVs = """
        #version 330 core
        layout(location = 0) in vec2  inPos;
        layout(location = 1) in vec3  inColorF;
        layout(location = 2) in float inClutF;
        layout(location = 3) in float inTexpageF;
        layout(location = 4) in vec2  inUV;
        layout(location = 5) in float inW;
        layout(location = 6) in float inZ;

        // vUV is the one thing that wants correcting: handing gl_Position a real W
        // makes the rasterizer interpolate it in 1/W, which is exactly the
        // perspective-correct mapping the PlayStation could not afford. vColor is
        // marked noperspective so that Gouraud shading stays as flat-interpolated
        // as the hardware's, and so an untextured primitive is bit-identical.
        noperspective out vec4 vColor;
        out vec2 vUV;
        out float vDepth;
        flat out ivec2 clutBase;
        flat out ivec2 pageBase;
        flat out int   texMode;
        flat out int   vDither;
        flat out int   vRepClut;

        uniform vec2 uVertexOffset;
        uniform vec2 uPosBias;
        uniform vec2 uFbInv;

        void main() {
            vec2 p = (inPos + uVertexOffset + uPosBias) * uFbInv - 1.0;
            // Exactly 1 is the "no depth was recovered" case, and it is written out
            // as the original expression rather than as p*1/1, so a primitive
            // without perspective data lands on the same pixels to the last bit.
            // Depth is a fragment value, not clip-space Z: these vertices are
            // already projected, and putting SZ into gl_Position.z lets OpenGL
            // clip them against a far plane the GPU never had — a hard line
            // across the floor where the cave used to continue.
            gl_Position = inW == 1.0 ? vec4(p, 0.0, 1.0) : vec4(p * inW, 0.0, inW);
            vDepth = inZ > 0.0 ? inZ * (1.0/65536.0) : 0.0;

            int inClut = int(inClutF + 0.5);
            int inTexpage = int(inTexpageF + 0.5);

            vColor = vec4(inColorF, 0.0) / 255.0;
            vDither = (inTexpage >> 10) & 1;
            vRepClut = (inTexpage >> 12) & 1;

            if ((inTexpage & 0x8000) != 0) {
                texMode = 4;
            } else if ((inTexpage & 0x4000) != 0) {
                texMode = 5;
                vUV = inUV;
            } else if ((inTexpage & 0x2000) != 0) {
                texMode = 6;
                vUV = inUV;
            } else {
                texMode = (inTexpage >> 7) & 3;
                vUV = inUV;
                pageBase = ivec2((inTexpage & 0xf) * 64, ((inTexpage >> 4) & 1) * 256);
                clutBase = ivec2((inClut & 0x3f) * 16, (inClut >> 6) & 0x1ff);
            }
        }
        """;

    public const string PrimFs = """
        #version 330 core
        noperspective in vec4 vColor;
        in vec2 vUV;
        in float vDepth;
        flat in ivec2 clutBase;
        flat in ivec2 pageBase;
        flat in int   texMode;
        flat in int   vDither;
        flat in int   vRepClut;

        layout(location = 0, index = 0) out vec4 FragColor;
        layout(location = 0, index = 1) out vec4 BlendColor;

        uniform sampler2D uVram;
        uniform sampler2D uDest;
        uniform sampler2D uExtTex;
        uniform sampler2D uRepTex;
        uniform sampler2D uRepClut;
        uniform vec4  uRepRect;
        uniform float uRepClutCount;
        uniform ivec4 uTexWindow;
        uniform vec4  uBlend;
        uniform vec4  uBlendOpaque = vec4(1.0, 1.0, 1.0, 0.0);
        uniform float uSetMask;
        uniform int   uCheckMask;
        uniform int   uScale;
        uniform vec2  uPosBias;

        const int ditherTbl[16] = int[16](
            -4,  0, -3,  1,
             2, -2,  3, -1,
            -3,  1, -4,  0,
             3, -1,  2, -2 );

        int u5(float f) { return int(floor(f * 31.0 + 0.5)); }
        vec4 fetch(ivec2 c) { return texelFetch(uVram, (c & ivec2(1023, 511)) * uScale, 0); }
        int fetch16(ivec2 c) {
            vec4 p = fetch(c);
            return u5(p.r) | (u5(p.g) << 5) | (u5(p.b) << 10) | (int(ceil(p.a)) << 15);
        }
        uniform float uTrueColor;
        vec3 quant5(ivec3 c8) {
            // True color: keep all eight bits, so the smooth shaded gradient is not
            // banded down to 32 levels. Dither is pointless here and skipped — the
            // RGBA8 target has nothing to dither into.
            if (uTrueColor > 0.5) return vec3(clamp(c8, 0, 255)) / 255.0;
            if (vDither != 0) {
                ivec2 vp = ivec2(floor(gl_FragCoord.xy / float(uScale) - uPosBias));
                c8 = clamp(c8 + ditherTbl[(vp.y & 3) * 4 + (vp.x & 3)], 0, 255);
            }
            return vec3(min(c8 >> 3, 31)) / 31.0;
        }

        void main() {
            // Written on every path so a 3D triangle's recovered SZ is the
            // window depth. Everything that recovered none writes the *far*
            // plane rather than the interpolated clip Z it used to, which is
            // the ambient-occlusion pass's whole mask: 2D, and any triangle the
            // vertex map missed, then say "no surface here" and are left alone
            // instead of being shaded against the geometry standing behind
            // them. The Z-buffer never saw the old value either -- a batch with
            // no recovered depth does not test and does not write -- so this
            // costs it nothing. Assigning this also turns off early-Z, so a
            // punch-through discard cannot occlude whatever is behind the hole.
            gl_FragDepth = vDepth > 0.0 ? vDepth : 1.0;
            if (uCheckMask != 0 && texelFetch(uDest, ivec2(gl_FragCoord.xy), 0).a >= 0.5) discard;

            if (texMode == 4) {
                FragColor = vec4(quant5(ivec3(vColor.rgb * 255.0 + 0.5)), uSetMask);
                BlendColor = uBlend;
                return;
            }

            if (texMode == 5) {
                vec4 img = texture(uExtTex, vUV);
                if (img.a < 0.5) discard;
                ivec3 e8 = (ivec3(img.rgb * 255.0 + 0.5) * ivec3(vColor.rgb * 255.0 + 0.5)) >> 7;
                FragColor = vec4(quant5(e8), uSetMask);
                BlendColor = uBlend;
                return;
            }

            int rawU = dFdx(vUV.x) < 0.0 ? int(ceil(vUV.x - 0.0001)) : int(floor(vUV.x + 0.0001));
            int rawV = dFdy(vUV.y) < 0.0 ? int(ceil(vUV.y - 0.0001)) : int(floor(vUV.y + 0.0001));
            ivec2 uv = (ivec2(rawU, rawV) & uTexWindow.xy) | uTexWindow.zw;
            uv &= ivec2(0xff);

            if (texMode == 6) {
                vec2 win = vec2(uTexWindow.xy) + 1.0;
                vec2 fuv = mod(vUV, win) + vec2(uTexWindow.zw);
                vec2 t = (fuv - uRepRect.xy) / uRepRect.zw;
                vec4 img = texture(uRepTex, t);
                if (img.a < 0.5) discard;
                ivec3 e8 = (ivec3(img.rgb * 255.0 + 0.5) * ivec3(vColor.rgb * 255.0 + 0.5)) >> 7;
                float stp = img.a < 0.95 ? 1.0 : 0.0;
                FragColor = vec4(quant5(e8), max(stp, uSetMask));
                BlendColor = stp > 0.5 ? uBlend : uBlendOpaque;
                return;
            }

            vec4 texel;

            if (texMode == 0) {
                int s = fetch16(ivec2(pageBase.x + (uv.x >> 2), pageBase.y + uv.y));
                int idx = (s >> ((uv.x & 3) << 2)) & 0xf;
                texel = vRepClut != 0
                    ? texture(uRepClut, vec2((float(idx) + 0.5) / uRepClutCount, 0.5))
                    : fetch(ivec2(clutBase.x + idx, clutBase.y));
            } else if (texMode == 1) {
                int s = fetch16(ivec2(pageBase.x + (uv.x >> 1), pageBase.y + uv.y));
                int idx = (s >> ((uv.x & 1) << 3)) & 0xff;
                texel = vRepClut != 0
                    ? texture(uRepClut, vec2((float(idx) + 0.5) / uRepClutCount, 0.5))
                    : fetch(ivec2(clutBase.x + idx, clutBase.y));
            } else {
                texel = fetch(ivec2(pageBase.x + uv.x, pageBase.y + uv.y));
            }

            if (vRepClut != 0 && texMode != 2) {
                if (texel.a < 0.5) discard;
                ivec3 e8 = (ivec3(texel.rgb * 255.0 + 0.5) * ivec3(vColor.rgb * 255.0 + 0.5)) >> 7;
                float stp = texel.a < 0.95 ? 1.0 : 0.0;
                FragColor = vec4(quant5(e8), max(stp, uSetMask));
                BlendColor = stp > 0.5 ? uBlend : uBlendOpaque;
                return;
            }

            if (texel.rgb == vec3(0.0) && texel.a < 0.5) discard;
            ivec3 t8 = ivec3(texel.rgb * 31.0 + 0.5) << 3;
            ivec3 c8 = (t8 * ivec3(vColor.rgb * 255.0 + 0.5)) >> 7;
            FragColor = vec4(quant5(c8), max(texel.a, uSetMask));
            BlendColor = texel.a >= 0.5 ? uBlend : uBlendOpaque;
        }
        """;
    
    public const string FullscreenVs120 = """
        #version 120
        attribute vec2 aPos;
        varying vec2 vUv;
        void main() {
            vUv = aPos * 0.5 + 0.5;
            gl_Position = vec4(aPos, 0.0, 1.0);
        }
        """;

    public const string PresentFs120 = """
        #version 120
        varying vec2 vUv;
        uniform sampler2D uVram;
        uniform vec2 uOrigin;
        uniform vec2 uSize;
        uniform vec2 uTexSize;
        void main() {
            vec2 t = (uOrigin + vUv * uSize) / uTexSize;
            gl_FragColor = vec4(texture2D(uVram, t).rgb, 1.0);
        }
        """;

    public const string Present24Fs120 = """
        #version 120
        varying vec2 vUv;
        uniform sampler2D uVram;
        uniform vec2 uOrigin;
        uniform vec2 uSize;
        uniform vec2 uVramSize;
        uniform float uScale;

        float u5(float f) { return floor(f * 31.0 + 0.5); }

        float texel16(float lin) {
            float x = mod(lin, 1024.0);
            float y = floor(lin / 1024.0);
            vec2 uv = (vec2(x, y) * uScale + 0.5) / uVramSize;
            vec4 p = texture2D(uVram, uv);
            return u5(p.r) + u5(p.g) * 32.0 + u5(p.b) * 1024.0 + ceil(p.a) * 32768.0;
        }

        float byteAt(float b) {
            float t = texel16(floor(b * 0.5));
            return mod(b, 2.0) < 0.5 ? mod(t, 256.0) : floor(t / 256.0);
        }

        void main() {
            float px = floor(vUv.x * uSize.x);
            float py = floor(vUv.y * uSize.y);
            float ty = uOrigin.y + py;
            float base = (ty * 1024.0 + uOrigin.x) * 2.0 + px * 3.0;
            gl_FragColor = vec4(byteAt(base) / 255.0, byteAt(base + 1.0) / 255.0, byteAt(base + 2.0) / 255.0, 1.0);
        }
        """;

    public const string BlitVs120 = """
        #version 120
        attribute vec2 aPos;
        uniform vec4 uDstRect;
        uniform vec4 uSrcRect;
        varying vec2 vSrc;
        void main() {
            vec2 unit = aPos * 0.5 + 0.5;
            vSrc = uSrcRect.xy + unit * uSrcRect.zw;
            vec2 p = uDstRect.xy + unit * uDstRect.zw;
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    public const string BlitFs120 = """
        #version 120
        varying vec2 vSrc;
        uniform sampler2D uSrc;
        void main() { gl_FragColor = texture2D(uSrc, vSrc); }
        """;

    public const string PrimVs120 = """
        #version 120
        attribute vec2  inPos;
        attribute vec3  inColorF;
        attribute float inClutF;
        attribute float inTexpageF;
        attribute vec2  inUV;
        attribute float inW;
        attribute float inZ;

        varying vec4  vColor;
        varying vec2  vUV;
        varying float vDepth;
        varying vec2  vClutBase;
        varying vec2  vPageBase;
        varying float vTexMode;
        varying float vDither;
        varying float vRepClut;

        uniform vec2 uVertexOffset;
        uniform vec2 uPosBias;
        uniform vec2 uFbInv;

        float bitAt(float v, float bit) { return floor(mod(v / bit, 2.0)); }

        void main() {
            vec2 p = (inPos + uVertexOffset + uPosBias) * uFbInv - 1.0;
            // As in the core profile: a real W makes vUV interpolate perspective
            // correctly. GLSL 120 has no `noperspective`, so on this backend alone
            // vColor is corrected along with it -- visible only as a slightly
            // different Gouraud gradient on a steeply angled textured polygon.
            // Depth is a fragment value, not clip-space Z: these vertices are
            // already projected, and putting SZ into gl_Position.z lets OpenGL
            // clip them against a far plane the GPU never had.
            gl_Position = inW == 1.0 ? vec4(p, 0.0, 1.0) : vec4(p * inW, 0.0, inW);
            vDepth = inZ > 0.0 ? inZ * (1.0/65536.0) : 0.0;

            float tp = floor(inTexpageF + 0.5);
            float clut = floor(inClutF + 0.5);

            vColor = vec4(inColorF / 255.0, 0.0);
            vDither = bitAt(tp, 1024.0);
            vRepClut = bitAt(tp, 4096.0);
            vUV = inUV;
            vClutBase = vec2(0.0);
            vPageBase = vec2(0.0);

            if (bitAt(tp, 32768.0) > 0.5) {
                vTexMode = 4.0;
            } else if (bitAt(tp, 16384.0) > 0.5) {
                vTexMode = 5.0;
            } else if (bitAt(tp, 8192.0) > 0.5) {
                vTexMode = 6.0;
            } else {
                vTexMode = floor(mod(tp / 128.0, 4.0));
                vPageBase = vec2(mod(tp, 16.0) * 64.0, bitAt(tp, 16.0) * 256.0);
                vClutBase = vec2(mod(clut, 64.0) * 16.0, mod(floor(clut / 64.0), 512.0));
            }
        }
        """;

    //gl 2.1 has no dual source blending =/ has to do by hand
    public const string PrimFs120 = """
        #version 120
        varying vec4  vColor;
        varying vec2  vUV;
        varying float vDepth;
        varying vec2  vClutBase;
        varying vec2  vPageBase;
        varying float vTexMode;
        varying float vDither;
        varying float vRepClut;

        uniform sampler2D uVram;
        uniform sampler2D uDest;
        uniform sampler2D uExtTex;
        uniform sampler2D uRepTex;
        uniform sampler2D uRepClut;
        uniform vec4  uRepRect;
        uniform float uRepClutCount;
        uniform vec4  uTexWindow;
        uniform float uSetMask;
        uniform float uCheckMask;
        uniform float uScale;
        uniform vec2  uPosBias;
        uniform vec2  uVramSize;
        uniform vec2  uDestSize;
        uniform float uSemiTrans;
        uniform float uBlendMode;

        float u5(float f) { return floor(f * 31.0 + 0.5); }

        vec4 fetch(vec2 c) {
            vec2 w = vec2(mod(c.x, 1024.0), mod(c.y, 512.0));
            return texture2D(uVram, (w * uScale + 0.5) / uVramSize);
        }

        float fetch16(vec2 c) {
            vec4 p = fetch(c);
            return u5(p.r) + u5(p.g) * 32.0 + u5(p.b) * 1024.0 + ceil(p.a) * 32768.0;
        }

        uniform float uTrueColor;
        vec3 quant5(vec3 c8) {
            if (uTrueColor > 0.5) return clamp(c8, 0.0, 255.0) / 255.0;
            if (vDither > 0.5) {
                vec2 vp = floor(gl_FragCoord.xy / uScale - uPosBias);
                float col = mod(vp.x, 4.0);
                float row = mod(vp.y, 4.0);
                float d = 0.0;
                if (row < 0.5)      d = col < 0.5 ? -4.0 : (col < 1.5 ?  0.0 : (col < 2.5 ? -3.0 :  1.0));
                else if (row < 1.5) d = col < 0.5 ?  2.0 : (col < 1.5 ? -2.0 : (col < 2.5 ?  3.0 : -1.0));
                else if (row < 2.5) d = col < 0.5 ? -3.0 : (col < 1.5 ?  1.0 : (col < 2.5 ? -4.0 :  0.0));
                else                d = col < 0.5 ?  3.0 : (col < 1.5 ? -1.0 : (col < 2.5 ?  2.0 : -2.0));
                c8 = clamp(c8 + d, 0.0, 255.0);
            }
            return min(floor(c8 / 8.0), 31.0) / 31.0;
        }

        vec3 blendWith(vec3 src, vec3 dst) {
            if (uBlendMode < 0.5) return (dst + src) * 0.5;
            if (uBlendMode < 1.5) return dst + src;
            if (uBlendMode < 2.5) return dst - src;
            return dst + src * 0.25;
        }

        void main() {
            gl_FragDepth = vDepth > 0.0 ? vDepth : 1.0;
            vec2 destUv = gl_FragCoord.xy / uDestSize;
            vec4 dstTexel = texture2D(uDest, destUv);
            if (uCheckMask > 0.5 && dstTexel.a >= 0.5) discard;

            vec3 rgb;
            float stp;
            float mask;

            if (vTexMode > 3.5 && vTexMode < 4.5) {
                rgb = vColor.rgb * 255.0;
                stp = 1.0;
                mask = uSetMask;
            } else if (vTexMode > 4.5 && vTexMode < 5.5) {
                vec4 img = texture2D(uExtTex, vUV);
                if (img.a < 0.5) discard;
                rgb = floor(img.rgb * 255.0 + 0.5) * floor(vColor.rgb * 255.0 + 0.5) / 128.0;
                stp = 1.0;
                mask = uSetMask;
            } else {
                vec2 win = uTexWindow.xy + 1.0;
                vec2 fuv = vec2(mod(vUV.x, win.x), mod(vUV.y, win.y)) + uTexWindow.zw;

        
                float rawU = dFdx(vUV.x) < 0.0 ? ceil(vUV.x - 0.0001) : floor(vUV.x + 0.0001);
                float rawV = dFdy(vUV.y) < 0.0 ? ceil(vUV.y - 0.0001) : floor(vUV.y + 0.0001);

                if (vTexMode > 5.5) {
                    vec2 t = (fuv - uRepRect.xy) / uRepRect.zw;
                    vec4 img = texture2D(uRepTex, t);
                    if (img.a < 0.5) discard;
                    rgb = floor(img.rgb * 255.0 + 0.5) * floor(vColor.rgb * 255.0 + 0.5) / 128.0;
                    stp = img.a < 0.95 ? 1.0 : 0.0;
                    mask = max(stp, uSetMask);
                } else {
                    vec2 uv = vec2(mod(rawU, win.x), mod(rawV, win.y)) + uTexWindow.zw;
                    uv = vec2(mod(uv.x, 256.0), mod(uv.y, 256.0));
                    vec4 texel;

                    if (vTexMode < 0.5) {
                        float s = fetch16(vec2(vPageBase.x + floor(uv.x / 4.0), vPageBase.y + uv.y));
                        float lane = mod(uv.x, 4.0);
                        float div = lane < 0.5 ? 1.0 : (lane < 1.5 ? 16.0 : (lane < 2.5 ? 256.0 : 4096.0));
                        float idx = mod(floor(s / div), 16.0);
                        texel = vRepClut > 0.5
                            ? texture2D(uRepClut, vec2((idx + 0.5) / uRepClutCount, 0.5))
                            : fetch(vec2(vClutBase.x + idx, vClutBase.y));
                    } else if (vTexMode < 1.5) {
                        float s = fetch16(vec2(vPageBase.x + floor(uv.x / 2.0), vPageBase.y + uv.y));
                        float div = mod(uv.x, 2.0) < 0.5 ? 1.0 : 256.0;
                        float idx = mod(floor(s / div), 256.0);
                        texel = vRepClut > 0.5
                            ? texture2D(uRepClut, vec2((idx + 0.5) / uRepClutCount, 0.5))
                            : fetch(vec2(vClutBase.x + idx, vClutBase.y));
                    } else {
                        texel = fetch(vec2(vPageBase.x + uv.x, vPageBase.y + uv.y));
                    }

                    if (vRepClut > 0.5 && vTexMode < 1.5) {
                        if (texel.a < 0.5) discard;
                        rgb = floor(texel.rgb * 255.0 + 0.5) * floor(vColor.rgb * 255.0 + 0.5) / 128.0;
                        stp = texel.a < 0.95 ? 1.0 : 0.0;
                    } else {
                        if (texel.r == 0.0 && texel.g == 0.0 && texel.b == 0.0 && texel.a < 0.5) discard;
                        vec3 t8 = floor(texel.rgb * 31.0 + 0.5) * 8.0;
                        rgb = t8 * floor(vColor.rgb * 255.0 + 0.5) / 128.0;
                        stp = texel.a >= 0.5 ? 1.0 : 0.0;
                    }
                    mask = max(stp, uSetMask);
                }
            }

            vec3 outRgb = quant5(floor(rgb));
            if (uSemiTrans * stp > 0.5) outRgb = clamp(blendWith(outRgb, dstTexel.rgb), 0.0, 1.0);

            gl_FragColor = vec4(outRgb, mask);
        }
        """;

    static readonly (uint Index, string Name)[] PrimAttribs =
    [
        (0, "inPos"), (1, "inColorF"), (2, "inClutF"), (3, "inTexpageF"), (4, "inUV"), (5, "inW"), (6, "inZ"),
    ];

    public static uint BuildPrim(GL gl, string vsSrc, string fsSrc, string name)
        => Build(gl, vsSrc, fsSrc, name, PrimAttribs);

    public static uint BuildFullscreen(GL gl, string vsSrc, string fsSrc, string name)
        => Build(gl, vsSrc, fsSrc, name, [(0, "aPos")]);

    public static uint Build(GL gl, string vsSrc, string fsSrc, string name, out string? error)
    {
        error = null;
        uint vs = CompileStage(gl, ShaderType.VertexShader, vsSrc, name, out string? vsLog);
        uint fs = CompileStage(gl, ShaderType.FragmentShader, fsSrc, name, out string? fsLog);
        if (vs == 0 || fs == 0)
        {
            error = vsLog ?? fsLog;
            if (vs != 0) gl.DeleteShader(vs);
            if (fs != 0) gl.DeleteShader(fs);
            return 0;
        }

        uint prog = gl.CreateProgram();
        gl.AttachShader(prog, vs);
        gl.AttachShader(prog, fs);
        gl.LinkProgram(prog);
        gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0)
        {
            error = gl.GetProgramInfoLog(prog);
            gl.DeleteProgram(prog);
            prog = 0;
        }
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        return prog;
    }

    static uint CompileStage(GL gl, ShaderType type, string src, string name, out string? log)
    {
        log = null;
        uint sh = gl.CreateShader(type);
        gl.ShaderSource(sh, Ascii(src));
        gl.CompileShader(sh);
        gl.GetShader(sh, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
        {
            log = $"{type}: {gl.GetShaderInfoLog(sh)}";
            gl.DeleteShader(sh);
            return 0;
        }
        return sh;
    }

    public static uint Build(GL gl, string vsSrc, string fsSrc, string name, (uint Index, string Name)[]? attribs = null)
    {
        uint vs = CompileStage(gl, ShaderType.VertexShader, vsSrc, name);
        uint fs = CompileStage(gl, ShaderType.FragmentShader, fsSrc, name);
        if (vs == 0 || fs == 0) return 0;

        uint prog = gl.CreateProgram();
        gl.AttachShader(prog, vs);
        gl.AttachShader(prog, fs);
        if (attribs != null)
            foreach (var (index, attrib) in attribs)
                gl.BindAttribLocation(prog, index, attrib);
        gl.LinkProgram(prog);
        gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0)
        {
            Console.WriteLine($"[GlBackend] link failed ({name}): {gl.GetProgramInfoLog(prog)}");
            gl.DeleteProgram(prog);
            prog = 0;
        }
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        return prog;
    }

    static string Ascii(string s)
    {
        var a = s.ToCharArray();
        for (int i = 0; i < a.Length; i++) if (a[i] > 0x7F) a[i] = ' ';
        return new string(a);
    }

    static uint CompileStage(GL gl, ShaderType type, string src, string name)
    {
        uint sh = gl.CreateShader(type);
        gl.ShaderSource(sh, Ascii(src));
        gl.CompileShader(sh);
        gl.GetShader(sh, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
        {
            Console.WriteLine($"[GlBackend] compile failed ({name} {type}) {gl.GetShaderInfoLog(sh)}");
            gl.DeleteShader(sh);
            return 0;
        }
        return sh;
    }
}
