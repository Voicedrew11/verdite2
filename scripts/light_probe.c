// light_probe -- run the port's real prim fragment shader over strips whose
// per-pixel lighting varyings sweep known values, headless, and compare every
// pixel with the formula (0048).
//
//     gcc -O0 -o /tmp/light_probe scripts/light_probe.c -lEGL -lGL -lm
//     /tmp/light_probe PrimFs.frag
//
// Extract the shader the way scripts/shader_probe.c's header says. The strips are
// untextured and true colour, so the pixel read back is the shader's 8-bit colour
// with nothing downstream of it. Each line reports the worst difference from the
// formula and how many distinct colours the strip holds; the exit code is the
// number of cases more than 1 out. KF2_PERPIXEL_PROBE=2 checks the same formula
// against the GTE in play, so between them the chain is closed. See "Per-pixel
// lighting" in docs/RENDERING.md.
#define GL_GLES_PROTOTYPES 0
#include <EGL/egl.h>
#include <GL/gl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

#define GL_COMPILE_STATUS 0x8B81
#define GL_LINK_STATUS 0x8B82
#define GL_VERTEX_SHADER 0x8B31
#define GL_FRAGMENT_SHADER 0x8B30
#define GL_ARRAY_BUFFER 0x8892
#define GL_STATIC_DRAW 0x88E4
#define GL_FRAMEBUFFER 0x8D40
#define GL_COLOR_ATTACHMENT0 0x8CE0
#define GL_FRAMEBUFFER_COMPLETE 0x8CD5

#define F(r,n,a) typedef r(*P_##n)a; P_##n n##_;
F(GLuint,glCreateShader,(GLenum)) F(void,glShaderSource,(GLuint,GLsizei,const char*const*,const GLint*))
F(void,glCompileShader,(GLuint)) F(void,glGetShaderiv,(GLuint,GLenum,GLint*))
F(void,glGetShaderInfoLog,(GLuint,GLsizei,GLsizei*,char*)) F(GLuint,glCreateProgram,(void))
F(void,glAttachShader,(GLuint,GLuint)) F(void,glLinkProgram,(GLuint))
F(void,glGetProgramiv,(GLuint,GLenum,GLint*)) F(void,glGetProgramInfoLog,(GLuint,GLsizei,GLsizei*,char*))
F(void,glUseProgram,(GLuint)) F(GLint,glGetUniformLocation,(GLuint,const char*))
F(void,glUniform1i,(GLint,GLint)) F(void,glUniform1f,(GLint,GLfloat)) F(void,glUniform1ui,(GLint,GLuint))
F(void,glUniform3f,(GLint,GLfloat,GLfloat,GLfloat))
F(void,glGenBuffers,(GLsizei,GLuint*)) F(void,glBindBuffer,(GLenum,GLuint))
F(void,glBufferData,(GLenum,GLsizeiptr,const void*,GLenum))
F(void,glGenVertexArrays,(GLsizei,GLuint*)) F(void,glBindVertexArray,(GLuint))
F(void,glEnableVertexAttribArray,(GLuint))
F(void,glVertexAttribPointer,(GLuint,GLint,GLenum,GLboolean,GLsizei,const void*))
F(void,glGenFramebuffers,(GLsizei,GLuint*)) F(void,glBindFramebuffer,(GLenum,GLuint))
F(void,glFramebufferTexture2D,(GLenum,GLenum,GLenum,GLuint,GLint))
F(GLenum,glCheckFramebufferStatus,(GLenum)) F(void,glBindAttribLocation,(GLuint,GLuint,const char*))

static void load(void){
#define L(n) n##_=(P_##n)eglGetProcAddress(#n); if(!n##_){printf("missing %s\n",#n);exit(2);}
 L(glCreateShader)L(glShaderSource)L(glCompileShader)L(glGetShaderiv)L(glGetShaderInfoLog)
 L(glCreateProgram)L(glAttachShader)L(glLinkProgram)L(glGetProgramiv)L(glGetProgramInfoLog)
 L(glUseProgram)L(glGetUniformLocation)L(glUniform1i)L(glUniform1f)L(glUniform1ui)L(glUniform3f)
 L(glGenBuffers)L(glBindBuffer)L(glBufferData)L(glGenVertexArrays)L(glBindVertexArray)
 L(glEnableVertexAttribArray)L(glVertexAttribPointer)L(glGenFramebuffers)L(glBindFramebuffer)
 L(glFramebufferTexture2D)L(glCheckFramebufferStatus)L(glBindAttribLocation)
}

static char *slurp(const char*p){FILE*f=fopen(p,"rb");if(!f){perror(p);exit(2);}
 fseek(f,0,SEEK_END);long n=ftell(f);fseek(f,0,SEEK_SET);char*b=malloc(n+1);
 if(fread(b,1,n,f)!=(size_t)n)exit(2);b[n]=0;fclose(f);return b;}

#define VW 256
static const char *VS =
"#version 330 core\n"
"layout(location=0) in vec2 aPos;\n"
"noperspective out vec4 vColor; out vec2 vUV; out float vDepth;\n"
"flat out ivec2 clutBase; flat out ivec2 pageBase; flat out int texMode;\n"
"flat out int vDither; flat out int vRepClut;\n"
"noperspective out vec3 vLit; noperspective out float vFog; flat out uint vLight;\n"
"uniform uint uTestLight; uniform vec3 uLit0, uLit1; uniform float uFog0, uFog1;\n"
"void main(){\n"
"  gl_Position = vec4(aPos,0.0,1.0);\n"
"  float t = aPos.x*0.5+0.5;\n"
"  vLit = mix(uLit0, uLit1, t); vFog = mix(uFog0, uFog1, t); vLight = uTestLight;\n"
"  vColor = vec4(1.0); vUV = vec2(0.0); vDepth = 0.0; clutBase = ivec2(0); pageBase = ivec2(0);\n"
"  texMode = 4; vDither = 0; vRepClut = 0;\n"
"}\n";

static const float BK[3]={1920,1920,1920};
static const float LCM[9]={2662,2662,3328, 2662,2662,3328, 2662,2662,3328};

static int expect(unsigned light, float lit[3], float fog, int ch){
 unsigned mode=light>>24; float l=lit[ch];
 if(mode&0x80){ float a[3]; for(int i=0;i<3;i++){a[i]=lit[i]<0?0:lit[i]>32767?32767:lit[i];}
   float ir=BK[ch]+(LCM[ch*3]*a[0]+LCM[ch*3+1]*a[1]+LCM[ch*3+2]*a[2])/4096.f;
   ir=ir<0?0:ir>32767?32767:ir; l=((light>>(8*ch))&255)*ir/4096.f; }
 float ir0=fog<0?0:fog>4096?4096:fog; float w; unsigned c=mode&7;
 w = c==1 ? (ir0-800>0?(ir0-800)*2:0) : c==2 ? (ir0<2800?ir0:3*ir0-5600) : c==3 ? ir0*0.5f : c==4 ? fog : 0;
 float v=floor(l*(1-w/4096.f)); return v<0?0:v>255?255:(int)v; }

int main(int argc,char**argv){
 EGLDisplay d=eglGetDisplay(EGL_DEFAULT_DISPLAY);
 if(!eglInitialize(d,NULL,NULL)){printf("egl init\n");return 2;}
 eglBindAPI(EGL_OPENGL_API);
 EGLint ca[]={EGL_SURFACE_TYPE,EGL_PBUFFER_BIT,EGL_RENDERABLE_TYPE,EGL_OPENGL_BIT,EGL_NONE};
 EGLConfig cfg;EGLint nc=0;eglChooseConfig(d,ca,&cfg,1,&nc);
 EGLint ctxa[]={EGL_CONTEXT_MAJOR_VERSION,3,EGL_CONTEXT_MINOR_VERSION,3,EGL_NONE};
 EGLContext ctx=eglCreateContext(d,cfg,EGL_NO_CONTEXT,ctxa);
 eglMakeCurrent(d,EGL_NO_SURFACE,EGL_NO_SURFACE,ctx);
 load();
 char*fs=slurp(argv[1]);
 GLuint v=glCreateShader_(GL_VERTEX_SHADER),f=glCreateShader_(GL_FRAGMENT_SHADER);
 glShaderSource_(v,1,&VS,NULL);glCompileShader_(v);
 glShaderSource_(f,1,(const char*const*)&fs,NULL);glCompileShader_(f);
 GLint ok; char log[8192]; GLsizei ln;
 glGetShaderiv_(v,GL_COMPILE_STATUS,&ok); if(!ok){glGetShaderInfoLog_(v,8192,&ln,log);printf("VS:%s\n",log);return 2;}
 glGetShaderiv_(f,GL_COMPILE_STATUS,&ok); if(!ok){glGetShaderInfoLog_(f,8192,&ln,log);printf("FS:%s\n",log);return 2;}
 GLuint p=glCreateProgram_();glAttachShader_(p,v);glAttachShader_(p,f);
 glBindAttribLocation_(p,0,"aPos"); glLinkProgram_(p);
 glGetProgramiv_(p,GL_LINK_STATUS,&ok); if(!ok){glGetProgramInfoLog_(p,8192,&ln,log);printf("LINK:%s\n",log);return 2;}
 glUseProgram_(p);
 GLint l;
 if((l=glGetUniformLocation_(p,"uTrueColor"))>=0) glUniform1f_(l,1.f); else {printf("no uTrueColor\n");return 2;}
 if((l=glGetUniformLocation_(p,"uSetMask"))>=0) glUniform1f_(l,0.f);
 if((l=glGetUniformLocation_(p,"uCheckMask"))>=0) glUniform1i_(l,0);
 if((l=glGetUniformLocation_(p,"uOpaqueDepth"))>=0) glUniform1i_(l,0);
 glUniform3f_(glGetUniformLocation_(p,"uLightBk"),BK[0],BK[1],BK[2]);
 glUniform3f_(glGetUniformLocation_(p,"uLcmR"),LCM[0],LCM[1],LCM[2]);
 glUniform3f_(glGetUniformLocation_(p,"uLcmG"),LCM[3],LCM[4],LCM[5]);
 glUniform3f_(glGetUniformLocation_(p,"uLcmB"),LCM[6],LCM[7],LCM[8]);

 GLuint rt; glGenTextures(1,&rt); glBindTexture(GL_TEXTURE_2D,rt);
 glTexImage2D(GL_TEXTURE_2D,0,GL_RGBA8,VW,1,0,GL_RGBA,GL_UNSIGNED_BYTE,NULL);
 GLuint fbo; glGenFramebuffers_(1,&fbo); glBindFramebuffer_(GL_FRAMEBUFFER,fbo);
 glFramebufferTexture2D_(GL_FRAMEBUFFER,GL_COLOR_ATTACHMENT0,GL_TEXTURE_2D,rt,0);
 if(glCheckFramebufferStatus_(GL_FRAMEBUFFER)!=GL_FRAMEBUFFER_COMPLETE){printf("fbo\n");return 2;}
 glViewport(0,0,VW,1);
 float quad[]={-1,-1, 3,-1, -1,3};
 GLuint vao,vbo; glGenVertexArrays_(1,&vao); glBindVertexArray_(vao);
 glGenBuffers_(1,&vbo); glBindBuffer_(GL_ARRAY_BUFFER,vbo);
 glBufferData_(GL_ARRAY_BUFFER,sizeof quad,quad,GL_STATIC_DRAW);
 glEnableVertexAttribArray_(0); glVertexAttribPointer_(0,2,GL_FLOAT,GL_FALSE,8,0);

 struct { const char*name; unsigned light; float l0[3], l1[3]; float f0, f1; } cases[] = {
  {"tile, knee",      (0x40u|2)<<24,            {200,120,60},{200,120,60}, -1500, 5500},
  {"tile, half",      (0x40u|3)<<24,            {255,255,255},{255,255,255}, -500, 5000},
  {"flat, offset",    (0x40u|1)<<24,            {400,300,90},{400,300,90}, 0, 4096},
  {"word",            (0x40u|4)<<24,            {180,180,180},{180,180,180}, 0, 6688},
  {"none",            (0x40u|0)<<24,            {300,20,255},{300,20,255}, 0, 4096},
  {"lit, knee",       ((0xC0u|2)<<24)|0x806040, {-4000,1500,3000},{4000,-2000,500}, 500, 3500},
  {"lit, bright",     ((0xC0u|0)<<24)|0xFFFFFF, {-8000,8000,-100},{9000,-500,12000}, 0, 0},
 };
 GLint uL=glGetUniformLocation_(p,"uTestLight"), uL0=glGetUniformLocation_(p,"uLit0"), uL1=glGetUniformLocation_(p,"uLit1");
 GLint uF0=glGetUniformLocation_(p,"uFog0"), uF1=glGetUniformLocation_(p,"uFog1");
 unsigned char px[VW*4]; int fails=0;
 for(unsigned k=0;k<sizeof cases/sizeof cases[0];k++){
   glUniform1ui_(uL,cases[k].light);
   glUniform3f_(uL0,cases[k].l0[0],cases[k].l0[1],cases[k].l0[2]);
   glUniform3f_(uL1,cases[k].l1[0],cases[k].l1[1],cases[k].l1[2]);
   glUniform1f_(uF0,cases[k].f0); glUniform1f_(uF1,cases[k].f1);
   glClearColor(0,0,0,1); glClear(GL_COLOR_BUFFER_BIT);
   glDrawArrays(GL_TRIANGLES,0,3);
   glReadPixels(0,0,VW,1,GL_RGBA,GL_UNSIGNED_BYTE,px);
   int worst=0, off1=0, distinct=0, last=-1;
   for(int x=0;x<VW;x++){
     float t=(x+0.5f)/VW; float lit[3]; for(int i=0;i<3;i++) lit[i]=cases[k].l0[i]+(cases[k].l1[i]-cases[k].l0[i])*t;
     float fog=cases[k].f0+(cases[k].f1-cases[k].f0)*t;
     for(int ch=0;ch<3;ch++){ int e=expect(cases[k].light,lit,fog,ch), g=px[x*4+ch]; int dd=abs(e-g); if(dd>worst)worst=dd; if(dd==1)off1++; }
     int g=px[x*4]|(px[x*4+1]<<8)|(px[x*4+2]<<16); if(g!=last){distinct++; last=g;}
   }
   printf("%-14s worst |shader - formula| %d, off by 1 in %d channel(s), %d distinct colours across %d px; first %d,%d,%d last %d,%d,%d\n",
     cases[k].name, worst, off1, distinct, VW, px[0],px[1],px[2], px[(VW-1)*4],px[(VW-1)*4+1],px[(VW-1)*4+2]);
   if(worst>1) fails++;
 }
 return fails;}
