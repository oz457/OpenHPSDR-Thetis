#ifndef _sbnr_h
#define _sbnr_h

#include <specbleach_adenoiser.h>

typedef struct _sbnr
{
	int run;
    	int position;
        double *in;
        double *out;
        float reduction_amount;
        float smoothing_factor;
        float whitening_factor;
        int noise_scaling_type;
        float noise_rescale;
        float post_filter_threshold;
        SpectralBleachHandle st;
        int buffer_size;

        float* input;
        float* output;
} sbnr, *SBNR;

// define the public api of this module
extern SBNR create_sbnr(int run, int position, double *in, double *out);
extern void destroy_sbnr(SBNR a);
extern void setSize_sbnr(SBNR a, int size);
extern void setBuffers_sbnr(SBNR a, double *in, double *out);
extern void xsbnr(SBNR a, int pos);

#endif // _sbnr_h
