#pragma once

#ifdef __cplusplus
extern "C" {
#endif

#ifdef _WIN32
#ifdef WB_NATIVE_EXPORTS
#define WB_API __declspec(dllexport)
#else
#define WB_API __declspec(dllimport)
#endif
#else
#define WB_API
#endif

typedef struct WbIntRect {
  int left;
  int top;
  int right;
  int bottom;
} WbIntRect;

typedef struct WbDetectRequest {
  const unsigned char* bgra;
  int width;
  int height;
  int stride;
  WbIntRect user_roi;
  float dpi_x;
  float dpi_y;
  int origin_x;
  int origin_y;
  const char* capture_id;
} WbDetectRequest;

typedef struct WbDetectResult {
  int status;
  WbIntRect workspace_capture;
  WbIntRect workspace_screen;
  int evidence_grade;
  float confidence;
  char message[256];
  char source_capture_id[64];
} WbDetectResult;

WB_API int wb_detect(const WbDetectRequest* req, WbDetectResult* result);
WB_API const char* wb_status_name(int status);

#ifdef __cplusplus
}
#endif
