#pragma once

#include <stdint.h>

#if defined(OTD_STROKE_STATIC)
#define OTD_STROKE_API
#elif defined(_WIN32)
#ifdef OTD_STROKE_EXPORTS
#define OTD_STROKE_API __declspec(dllexport)
#else
#define OTD_STROKE_API __declspec(dllimport)
#endif
#else
#define OTD_STROKE_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct OtdStrokeHandle OtdStrokeHandle;

/* Create recorder rooted at otdRoot (UTF-8). Creates <otdRoot>/stroke automatically. */
OTD_STROKE_API OtdStrokeHandle* otd_stroke_create(const char* otdRootUtf8, const char* deviceNameUtf8,
                                                   const char* deviceIdUtf8);

OTD_STROKE_API void otd_stroke_destroy(OtdStrokeHandle* handle);

OTD_STROKE_API int otd_stroke_start(OtdStrokeHandle* handle);
OTD_STROKE_API void otd_stroke_stop(OtdStrokeHandle* handle);

OTD_STROKE_API void otd_stroke_pen_down(OtdStrokeHandle* handle);
OTD_STROKE_API void otd_stroke_pen_up(OtdStrokeHandle* handle);

/*
 * Feed one sample. timestamps/sequence may be 0 to auto-fill.
 * inContact non-zero means tip contact.
 */
OTD_STROKE_API void otd_stroke_on_point(OtdStrokeHandle* handle, uint64_t timestampMs, double x,
                                        double y, double pressure, int inContact, uint32_t buttons,
                                        double tiltX, double tiltY, uint64_t sequenceId);

/* Evaluate pen-up timeout (>=500ms). Pass wall-clock ms or 0 for now. */
OTD_STROKE_API void otd_stroke_tick(OtdStrokeHandle* handle, uint64_t nowMs);

OTD_STROKE_API int otd_stroke_state(OtdStrokeHandle* handle);
OTD_STROKE_API int otd_stroke_current_path(OtdStrokeHandle* handle, char* outUtf8, int outCap);

/* Convert .strokebin to JSON file. Returns non-zero on success. */
OTD_STROKE_API int otd_stroke_export_json(const char* strokebinPathUtf8, const char* jsonPathUtf8);

#ifdef __cplusplus
}
#endif
