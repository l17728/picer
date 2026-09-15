#pragma once

#include <stdint.h>

#if defined(_WIN32)
#define PICER_AEC3_API extern "C" __declspec(dllexport)
#else
#define PICER_AEC3_API extern "C"
#endif

// All audio is signed 16-bit, mono PCM. Each call processes exactly 10 ms.
PICER_AEC3_API void* picer_aec3_create(int capture_sample_rate_hz,
                                        int render_sample_rate_hz);
PICER_AEC3_API void picer_aec3_destroy(void* handle);
PICER_AEC3_API int picer_aec3_process_render(void* handle,
                                             const int16_t* samples,
                                             int sample_count);
PICER_AEC3_API int picer_aec3_process_capture(void* handle,
                                              int16_t* samples,
                                              int sample_count,
                                              int stream_delay_ms);
PICER_AEC3_API const char* picer_aec3_version();
