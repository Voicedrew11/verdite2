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
        uniform vec2 uOrigin;
        uniform vec2 uSize;
        uniform vec2 uTexSize;
        out vec4 oColor;
        void main() {
            vec2 t = (uOrigin + vUv * uSize) / uTexSize;
            oColor = vec4(texture(uVram, t).rgb, 1.0);
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
        uniform float uAniso;

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
        // One texel, exactly as the unfiltered path reads it: texture window, the
        // 8-bit page wrap, the page fetch, the nibble/byte extract and the CLUT
        // lookup. A paletted texel is an *index*, so none of this can be filtered
        // before the lookup -- the average of index 3 and index 4 is index 3.5,
        // a different colour with no relation to either. The kernel below calls
        // this per tap, which is what puts the filter after the palette.
        vec4 decode(ivec2 raw) {
            ivec2 uv = ((raw & uTexWindow.xy) | uTexWindow.zw) & ivec2(0xff);
            if (texMode == 0) {
                int s = fetch16(ivec2(pageBase.x + (uv.x >> 2), pageBase.y + uv.y));
                int idx = (s >> ((uv.x & 3) << 2)) & 0xf;
                return vRepClut != 0
                    ? texture(uRepClut, vec2((float(idx) + 0.5) / uRepClutCount, 0.5))
                    : fetch(ivec2(clutBase.x + idx, clutBase.y));
            } else if (texMode == 1) {
                int s = fetch16(ivec2(pageBase.x + (uv.x >> 1), pageBase.y + uv.y));
                int idx = (s >> ((uv.x & 1) << 3)) & 0xff;
                return vRepClut != 0
                    ? texture(uRepClut, vec2((float(idx) + 0.5) / uRepClutCount, 0.5))
                    : fetch(ivec2(clutBase.x + idx, clutBase.y));
            }
            return fetch(ivec2(pageBase.x + uv.x, pageBase.y + uv.y));
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
            // window depth, and a 2D primitive (vDepth == 0, test off) keeps
            // the interpolated clip Z it has always had. Assigning this also
            // turns off early-Z, so a punch-through discard cannot occlude
            // whatever is behind the hole.
            gl_FragDepth = vDepth > 0.0 ? vDepth : gl_FragCoord.z;
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

            // The two screen derivatives of the texture coordinate span the
            // pixel's footprint in texture space. They already decided which way
            // the console's truncation rounds; the kernel below reads the same
            // pair as the shape of the area this pixel actually covers.
            vec2 dUVdx = dFdx(vUV);
            vec2 dUVdy = dFdy(vUV);
            int rawU = dUVdx.x < 0.0 ? int(ceil(vUV.x - 0.0001)) : int(floor(vUV.x + 0.0001));
            int rawV = dUVdy.y < 0.0 ? int(ceil(vUV.y - 0.0001)) : int(floor(vUV.y + 0.0001));

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

            vec4 texel = decode(ivec2(rawU, rawV));

            // Anisotropic filtering. The footprint is a square looked at square-on
            // and a long thin sliver on a floor running away to the horizon, and
            // one point sample of a sliver is what makes such a floor crawl and
            // sparkle as the camera moves. Sample along the sliver's long axis
            // instead -- one tap per texel it spans, up to uAniso of them -- and
            // average. Each tap truncates the way the hardware did, so the short
            // axis stays exactly as hard as the console left it and only the axis
            // that was being undersampled is averaged.
            //
            // This needs no test for "is this 3D": a HUD sprite, a menu box or a
            // font glyph is drawn at or near 1:1 and axis-aligned, so both
            // derivatives are about one texel, the axis spans one texel, taps
            // comes out 1 and the fragment takes the line above unchanged.
            if (uAniso > 1.5 && vRepClut == 0) {
                vec2 axis = dot(dUVdx, dUVdx) >= dot(dUVdy, dUVdy) ? dUVdx : dUVdy;
                int taps = int(min(ceil(length(axis)), uAniso));
                if (taps > 1) {
                    vec3 sum = vec3(0.0);
                    float solid = 0.0;
                    for (int i = 0; i < 16; ++i) {
                        if (i >= taps) break;
                        vec2 t = vUV + axis * ((float(i) + 0.5) / float(taps) - 0.5);
                        vec4 c = decode(ivec2(
                            dUVdx.x < 0.0 ? int(ceil(t.x - 0.0001)) : int(floor(t.x + 0.0001)),
                            dUVdy.y < 0.0 ? int(ceil(t.y - 0.0001)) : int(floor(t.y + 0.0001))));
                        // A transparent texel is stored as black with the STP bit
                        // clear, so averaging it in as a colour draws a dark fringe
                        // round every grate, torch and bush in the game. Weigh it
                        // zero and renormalise by what survived.
                        float w = (c.rgb == vec3(0.0) && c.a < 0.5) ? 0.0 : 1.0;
                        sum += c.rgb * w;
                        solid += w;
                    }
                    // Half coverage is where truncation put the silhouette, so the
                    // punch-through edge neither grows nor shrinks. The semi-
                    // transparency bit selects a blend equation rather than being a
                    // colour, so it is taken whole from the centre tap -- the texel
                    // the unfiltered path would have read -- and never averaged.
                    if (solid * 2.0 < float(taps)) discard;
                    texel = vec4(sum / solid, texel.a);
                }
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
        uniform float uAniso;

        float u5(float f) { return floor(f * 31.0 + 0.5); }

        vec4 fetch(vec2 c) {
            vec2 w = vec2(mod(c.x, 1024.0), mod(c.y, 512.0));
            return texture2D(uVram, (w * uScale + 0.5) / uVramSize);
        }

        float fetch16(vec2 c) {
            vec4 p = fetch(c);
            return u5(p.r) + u5(p.g) * 32.0 + u5(p.b) * 1024.0 + ceil(p.a) * 32768.0;
        }

        // One texel, the whole per-texel job -- see the core-profile shader for
        // why a paletted texel cannot be filtered before the CLUT lookup.
        vec4 decode(vec2 raw) {
            vec2 win = uTexWindow.xy + 1.0;
            vec2 uv = vec2(mod(raw.x, win.x), mod(raw.y, win.y)) + uTexWindow.zw;
            uv = vec2(mod(uv.x, 256.0), mod(uv.y, 256.0));
            if (vTexMode < 0.5) {
                float s = fetch16(vec2(vPageBase.x + floor(uv.x / 4.0), vPageBase.y + uv.y));
                float lane = mod(uv.x, 4.0);
                float div = lane < 0.5 ? 1.0 : (lane < 1.5 ? 16.0 : (lane < 2.5 ? 256.0 : 4096.0));
                float idx = mod(floor(s / div), 16.0);
                return vRepClut > 0.5
                    ? texture2D(uRepClut, vec2((idx + 0.5) / uRepClutCount, 0.5))
                    : fetch(vec2(vClutBase.x + idx, vClutBase.y));
            } else if (vTexMode < 1.5) {
                float s = fetch16(vec2(vPageBase.x + floor(uv.x / 2.0), vPageBase.y + uv.y));
                float div = mod(uv.x, 2.0) < 0.5 ? 1.0 : 256.0;
                float idx = mod(floor(s / div), 256.0);
                return vRepClut > 0.5
                    ? texture2D(uRepClut, vec2((idx + 0.5) / uRepClutCount, 0.5))
                    : fetch(vec2(vClutBase.x + idx, vClutBase.y));
            }
            return fetch(vec2(vPageBase.x + uv.x, vPageBase.y + uv.y));
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
            gl_FragDepth = vDepth > 0.0 ? vDepth : gl_FragCoord.z;
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

        
                vec2 dUVdx = dFdx(vUV);
                vec2 dUVdy = dFdy(vUV);
                float rawU = dUVdx.x < 0.0 ? ceil(vUV.x - 0.0001) : floor(vUV.x + 0.0001);
                float rawV = dUVdy.y < 0.0 ? ceil(vUV.y - 0.0001) : floor(vUV.y + 0.0001);

                if (vTexMode > 5.5) {
                    vec2 t = (fuv - uRepRect.xy) / uRepRect.zw;
                    vec4 img = texture2D(uRepTex, t);
                    if (img.a < 0.5) discard;
                    rgb = floor(img.rgb * 255.0 + 0.5) * floor(vColor.rgb * 255.0 + 0.5) / 128.0;
                    stp = img.a < 0.95 ? 1.0 : 0.0;
                    mask = max(stp, uSetMask);
                } else {
                    vec4 texel = decode(vec2(rawU, rawV));

                    // Anisotropic filtering -- see the core-profile shader for what
                    // the footprint is and why the transparent texels are weighed
                    // out. Identical arithmetic; only the types differ.
                    if (uAniso > 1.5 && vRepClut < 0.5) {
                        vec2 axis = dot(dUVdx, dUVdx) >= dot(dUVdy, dUVdy) ? dUVdx : dUVdy;
                        float taps = min(ceil(length(axis)), uAniso);
                        if (taps > 1.5) {
                            vec3 sum = vec3(0.0);
                            float solid = 0.0;
                            for (int i = 0; i < 16; ++i) {
                                if (float(i) >= taps) break;
                                vec2 t = vUV + axis * ((float(i) + 0.5) / taps - 0.5);
                                vec4 c = decode(vec2(
                                    dUVdx.x < 0.0 ? ceil(t.x - 0.0001) : floor(t.x + 0.0001),
                                    dUVdy.y < 0.0 ? ceil(t.y - 0.0001) : floor(t.y + 0.0001)));
                                float w = (c.r == 0.0 && c.g == 0.0 && c.b == 0.0 && c.a < 0.5)
                                    ? 0.0 : 1.0;
                                sum += c.rgb * w;
                                solid += w;
                            }
                            if (solid * 2.0 < taps) discard;
                            texel = vec4(sum / solid, texel.a);
                        }
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
