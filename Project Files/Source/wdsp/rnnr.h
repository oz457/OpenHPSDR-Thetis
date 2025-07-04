#ifndef _rnnr_h
#define _rnnr_h

#include "rnnoise.h"

#define FRAME_SIZE

typedef struct _queuenode {
    float value;
    struct _queuenode* next;
} queuenode;

typedef struct _rnnr
{
	int run;
    	int position;
        int frame_size;
        DenoiseState *st;
        double *in;
        double *out;

        //MW0LGE
        int buffer_size;
        float* output_buffer;
        float gain;

        float* to_process_buffer;
        float* processed_output_buffer;

        queuenode* input_queue_head;
        queuenode* input_queue_tail;
        int input_queue_count;

        queuenode* output_queue_head;
        queuenode* output_queue_tail;
        int output_queue_count;

}rnnr, *RNNR;

extern RNNR create_rnnr (int run, int position, double *in, double *out);
extern void setSize_rnnr(RNNR a, int size);
extern void setBuffers_rnnr (RNNR a, double* in, double* out);
extern void destroy_rnnr (RNNR a);
extern void xrnnr (RNNR a, int pos);

#endif //_rnnr_h
