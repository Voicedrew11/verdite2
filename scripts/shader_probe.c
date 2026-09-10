// shader_probe -- run one of the port's real prim fragment shaders over a known
// texture with a known footprint, headless, and read the pixels back.
//
//     gcc -O0 -o /tmp/shader_probe scripts/shader_probe.c -lEGL -lGL -lm
//     FOOT=16 /tmp/shader_probe PrimFs.frag 1 2 4 8 16
//
// Extract the shader with the snippet in "Anisotropic filtering" in
// docs/RENDERING.md; FOOT is the footprint in texels per output pixel along U.
//
// Why this exists. A picture switch in this port can be dead and still report
// itself on -- the RAM fast path went round the vertex map's hooks, and a hook
// summary counted registrations rather than detours -- and frame rate cannot tell
// the difference, because the port is CPU-bound at ~860 fps and the whole GPU cost
// of a filter hides under that. Nothing else here can answer "does the kernel
// actually change a pixel". This drives the real fragment shader with a substitute
// vertex shader that supplies its varyings, so the thing under test is the shipped
// code rather than a copy of it.
//
// The texture is value noise, because the statistic that matters is the *spread*
// across neighbouring pixels: pixels covering nearly the same texels returning
// wildly different colours is exactly the sparkle anisotropic filtering exists to
// remove, so the collapse of the standard deviation is the measurement. Stripes
// were tried first and are a trap -- a period-2 pattern against an even tap
// spacing puts every tap on one parity, which reads as the filter doing nothing at
// 2 and 4 taps and as a wild result at 8.
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
#define GL_RGBA8 0x8058
#define GL_TEXTURE0 0x84C0
#define GL_CLAMP_TO_EDGE 0x812F
#define GL_FRAMEBUFFER_COMPLETE 0x8CD5

#define F(r,n,a) typedef r(*P_##n)a; P_##n n##_;
F(GLuint,glCreateShader,(GLenum)) F(void,glShaderSource,(GLuint,GLsizei,const char*const*,const GLint*))
F(void,glCompileShader,(GLuint)) F(void,glGetShaderiv,(GLuint,GLenum,GLint*))
F(void,glGetShaderInfoLog,(GLuint,GLsizei,GLsizei*,char*)) F(GLuint,glCreateProgram,(void))
F(void,glAttachShader,(GLuint,GLuint)) F(void,glLinkProgram,(GLuint))
F(void,glGetProgramiv,(GLuint,GLenum,GLint*)) F(void,glGetProgramInfoLog,(GLuint,GLsizei,GLsizei*,char*))
F(void,glUseProgram,(GLuint)) F(GLint,glGetUniformLocation,(GLuint,const char*))
F(void,glUniform1i,(GLint,GLint)) F(void,glUniform1f,(GLint,GLfloat))
F(void,glUniform4i,(GLint,GLint,GLint,GLint,GLint)) F(void,glUniform4f,(GLint,GLfloat,GLfloat,GLfloat,GLfloat))
F(void,glUniform2f,(GLint,GLfloat,GLfloat))
F(void,glGenBuffers,(GLsizei,GLuint*)) F(void,glBindBuffer,(GLenum,GLuint))
F(void,glBufferData,(GLenum,GLsizeiptr,const void*,GLenum))
F(void,glGenVertexArrays,(GLsizei,GLuint*)) F(void,glBindVertexArray,(GLuint))
F(void,glEnableVertexAttribArray,(GLuint))
F(void,glVertexAttribPointer,(GLuint,GLint,GLenum,GLboolean,GLsizei,const void*))
F(void,glGenFramebuffers,(GLsizei,GLuint*)) F(void,glBindFramebuffer,(GLenum,GLuint))
F(void,glFramebufferTexture2D,(GLenum,GLenum,GLenum,GLuint,GLint))
F(GLenum,glCheckFramebufferStatus,(GLenum)) F(void,glActiveTexture,(GLenum))
F(void,glBindAttribLocation,(GLuint,GLuint,const char*))

static void load(void){
#define L(n) n##_=(P_##n)eglGetProcAddress(#n); if(!n##_){printf("missing %s\n",#n);exit(2);}
 L(glCreateShader)L(glShaderSource)L(glCompileShader)L(glGetShaderiv)L(glGetShaderInfoLog)
 L(glCreateProgram)L(glAttachShader)L(glLinkProgram)L(glGetProgramiv)L(glGetProgramInfoLog)
 L(glUseProgram)L(glGetUniformLocation)L(glUniform1i)L(glUniform1f)L(glUniform4i)L(glUniform4f)
 L(glUniform2f)L(glGenBuffers)L(glBindBuffer)L(glBufferData)L(glGenVertexArrays)L(glBindVertexArray)
 L(glEnableVertexAttribArray)L(glVertexAttribPointer)L(glGenFramebuffers)L(glBindFramebuffer)
 L(glFramebufferTexture2D)L(glCheckFramebufferStatus)L(glActiveTexture)L(glBindAttribLocation)
}

static char *slurp(const char*p){FILE*f=fopen(p,"rb");if(!f){perror(p);exit(2);}
 fseek(f,0,SEEK_END);long n=ftell(f);fseek(f,0,SEEK_SET);char*b=malloc(n+1);
 if(fread(b,1,n,f)!=(size_t)n)exit(2);b[n]=0;fclose(f);return b;}

// vUV.x sweeps FOOT texels per output pixel; vUV.y is constant, so the footprint
// is a pure horizontal sliver -- the exact case anisotropy exists for.
#define VW 64
#define FOOT 16
static const char *TESTVS_T =
"#version 330 core\n"
"layout(location=0) in vec2 aPos;\n"
"noperspective out vec4 vColor; out vec2 vUV; out float vDepth;\n"
"flat out ivec2 clutBase; flat out ivec2 pageBase; flat out int texMode;\n"
"flat out int vDither; flat out int vRepClut;\n"
"void main(){\n"
"  gl_Position = vec4(aPos,0.0,1.0);\n"
"  float px = (aPos.x*0.5+0.5)*64.0;\n"
"  vUV = vec2(px*FOOTV, 8.0);\n"
"  vColor = vec4(vec3(128.0/255.0),1.0);\n"  // neutral through the >>7 modulate
"  vDepth = 0.0; clutBase = ivec2(0); pageBase = ivec2(0);\n"
"  texMode = 2; vDither = 0; vRepClut = 0;\n"
"}\n";

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
 double foot=getenv("FOOT")?atof(getenv("FOOT")):16.0;
 char TESTVSBUF[4096]; { char tmp[4096]; snprintf(tmp,sizeof tmp,"%s",TESTVS_T);
   char*q=strstr(tmp,"FOOTV"); char head[4096]; size_t hn=q-tmp; memcpy(head,tmp,hn); head[hn]=0;
   snprintf(TESTVSBUF,sizeof TESTVSBUF,"%s%.6f%s",head,foot,q+5); }
 const char*TESTVS=TESTVSBUF;
 GLuint v=glCreateShader_(GL_VERTEX_SHADER),f=glCreateShader_(GL_FRAGMENT_SHADER);
 glShaderSource_(v,1,&TESTVS,NULL);glCompileShader_(v);
 glShaderSource_(f,1,(const char*const*)&fs,NULL);glCompileShader_(f);
 GLint ok; char log[8192]; GLsizei ln;
 glGetShaderiv_(v,GL_COMPILE_STATUS,&ok); if(!ok){glGetShaderInfoLog_(v,8192,&ln,log);printf("VS:%s\n",log);return 2;}
 glGetShaderiv_(f,GL_COMPILE_STATUS,&ok); if(!ok){glGetShaderInfoLog_(f,8192,&ln,log);printf("FS:%s\n",log);return 2;}
 GLuint p=glCreateProgram_();glAttachShader_(p,v);glAttachShader_(p,f);
 glBindAttribLocation_(p,0,"aPos");
 glLinkProgram_(p);
 glGetProgramiv_(p,GL_LINK_STATUS,&ok); if(!ok){glGetProgramInfoLog_(p,8192,&ln,log);printf("LINK:%s\n",log);return 2;}
 glUseProgram_(p);

 // VRAM sheet: one-texel vertical stripes, white / mid-grey. Neither is rgb==0,
 // so both are solid and the coverage rule leaves them alone.
 unsigned char *vram=malloc(1024*512*4);
 for(int y=0;y<512;y++)for(int x=0;x<1024;x++){
   unsigned int h=(unsigned)x*2654435761u ^ (unsigned)y*40503u; h^=h>>13; h*=1274126177u; h^=h>>16;
   unsigned char c=(unsigned char)(64+(h%192)); unsigned char*t=vram+((size_t)y*1024+x)*4;
   t[0]=t[1]=t[2]=c; t[3]=0; }
 GLuint tex; glGenTextures(1,&tex);
 glActiveTexture_(GL_TEXTURE0); glBindTexture(GL_TEXTURE_2D,tex);
 glTexImage2D(GL_TEXTURE_2D,0,GL_RGBA8,1024,512,0,GL_RGBA,GL_UNSIGNED_BYTE,vram);
 glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MIN_FILTER,GL_NEAREST);
 glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MAG_FILTER,GL_NEAREST);
 for(int u=0;u<5;u++){glActiveTexture_(GL_TEXTURE0+u);glBindTexture(GL_TEXTURE_2D,tex);}
 glActiveTexture_(GL_TEXTURE0);

 const char*sn[]={"uVram","uDest","uExtTex","uRepTex","uRepClut"};
 for(int i=0;i<5;i++){GLint l=glGetUniformLocation_(p,sn[i]); if(l>=0) glUniform1i_(l,i);}
 GLint l;
 if((l=glGetUniformLocation_(p,"uTexWindow"))>=0) glUniform4i_(l,255,255,0,0);
 if((l=glGetUniformLocation_(p,"uScale"))>=0) glUniform1i_(l,1);
 if((l=glGetUniformLocation_(p,"uSetMask"))>=0) glUniform1f_(l,0.f);
 if((l=glGetUniformLocation_(p,"uCheckMask"))>=0) glUniform1i_(l,0);
 if((l=glGetUniformLocation_(p,"uTrueColor"))>=0) glUniform1f_(l,0.f);
 if((l=glGetUniformLocation_(p,"uPosBias"))>=0) glUniform2f_(l,0.f,0.f);
 if((l=glGetUniformLocation_(p,"uBlend"))>=0) glUniform4f_(l,1,1,1,0);
 if((l=glGetUniformLocation_(p,"uRepClutCount"))>=0) glUniform1f_(l,16.f);
 if((l=glGetUniformLocation_(p,"uRepRect"))>=0) glUniform4f_(l,0,0,1,1);
 GLint uAniso=glGetUniformLocation_(p,"uAniso");
 if(uAniso<0){printf("uAniso NOT PRESENT\n");return 1;}

 GLuint rt; glGenTextures(1,&rt); glBindTexture(GL_TEXTURE_2D,rt);
 glTexImage2D(GL_TEXTURE_2D,0,GL_RGBA8,VW,4,0,GL_RGBA,GL_UNSIGNED_BYTE,NULL);
 glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MIN_FILTER,GL_NEAREST);
 glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MAG_FILTER,GL_NEAREST);
 GLuint fbo; glGenFramebuffers_(1,&fbo); glBindFramebuffer_(GL_FRAMEBUFFER,fbo);
 glFramebufferTexture2D_(GL_FRAMEBUFFER,GL_COLOR_ATTACHMENT0,GL_TEXTURE_2D,rt,0);
 if(glCheckFramebufferStatus_(GL_FRAMEBUFFER)!=GL_FRAMEBUFFER_COMPLETE){printf("fbo\n");return 2;}
 glViewport(0,0,VW,4);
 glActiveTexture_(GL_TEXTURE0); glBindTexture(GL_TEXTURE_2D,tex);

 float quad[]={-1,-1, 3,-1, -1,3};
 GLuint vao,vbo; glGenVertexArrays_(1,&vao); glBindVertexArray_(vao);
 glGenBuffers_(1,&vbo); glBindBuffer_(GL_ARRAY_BUFFER,vbo);
 glBufferData_(GL_ARRAY_BUFFER,sizeof quad,quad,GL_STATIC_DRAW);
 glEnableVertexAttribArray_(0); glVertexAttribPointer_(0,2,GL_FLOAT,GL_FALSE,8,0);

 printf("footprint = %.3f texels/pixel along U, 0 along V\n",foot);
 unsigned char px[VW*4*4];
 for(int i=1;i<argc-1;i++){
   int a=atoi(argv[i+1]);
   glUniform1f_(uAniso,(float)a);
   glClearColor(0,0,0,1); glClear(GL_COLOR_BUFFER_BIT);
   glDrawArrays(GL_TRIANGLES,0,3);
   glReadPixels(0,0,VW,4,GL_RGBA,GL_UNSIGNED_BYTE,px);
   int lo=255,hi=0; double sum=0,sq=0; int n=VW-16;
   for(int x=8;x<VW-8;x++){int c=px[(2*VW+x)*4]; if(c<lo)lo=c; if(c>hi)hi=c; sum+=c; sq+=(double)c*c;}
   double mean=sum/n, sd=sqrt(sq/n-mean*mean);
   printf("uAniso=%-3d  min %3d  max %3d  mean %6.2f  spread(sd) %6.2f  range %3d\n",
          a,lo,hi,mean,sd,hi-lo);
 }
 return 0;}
