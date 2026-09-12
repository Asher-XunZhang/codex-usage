#include "UsageNative.h"
#include <CommonCrypto/CommonDigest.h>
#include <stdio.h>
#include <string.h>
static void hex(const unsigned char *bytes,char output[65]) { static const char digits[]="0123456789abcdef"; for(int i=0;i<32;i++){output[i*2]=digits[bytes[i]>>4];output[i*2+1]=digits[bytes[i]&15];}output[64]=0; }
void usage_sha256(const char *text,char output[65]) { unsigned char digest[32]; CC_SHA256(text,(CC_LONG)strlen(text),digest); hex(digest,output); }
int usage_sha256_file(const char *path,char output[65]) { FILE *file=fopen(path,"rb"); if(!file)return -1; CC_SHA256_CTX context; CC_SHA256_Init(&context); unsigned char buffer[65536],digest[32]; size_t length; while((length=fread(buffer,1,sizeof(buffer),file))>0)CC_SHA256_Update(&context,buffer,(CC_LONG)length);int failed=ferror(file);fclose(file);if(failed)return -1;CC_SHA256_Final(digest,&context);hex(digest,output);return 0; }
