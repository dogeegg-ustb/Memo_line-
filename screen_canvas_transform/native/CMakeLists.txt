cmake_minimum_required(VERSION 3.20)
project(ScreenCanvasNative LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# workspace_border_detect sources are compiled into this library (no wb C API / DLL).
add_library(ScreenCanvasNative SHARED
  src/color.cpp
  src/features.cpp
  src/seeds.cpp
  src/background.cpp
  src/similarity.cpp
  src/grower.cpp
  src/geometry.cpp
  src/scoring.cpp
  src/refine.cpp
  src/validate.cpp
  src/detector.cpp
  src/canvas_observe.cpp
  src/viewport_frame.cpp
  src/transform_solve.cpp
  src/sct_c_api.cpp
)

target_include_directories(ScreenCanvasNative PUBLIC
  ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_compile_definitions(ScreenCanvasNative PRIVATE SCT_NATIVE_EXPORTS)

if(MSVC)
  target_compile_options(ScreenCanvasNative PRIVATE /W3 /EHsc /utf-8)
  set_property(TARGET ScreenCanvasNative PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()
