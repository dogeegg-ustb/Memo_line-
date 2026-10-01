cmake_minimum_required(VERSION 3.20)
project(ScreenCanvasNative LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(WORKSPACE_BORDER_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/workspace_border")

add_library(ScreenCanvasNative SHARED
  ${WORKSPACE_BORDER_ROOT}/src/color.cpp
  ${WORKSPACE_BORDER_ROOT}/src/features.cpp
  ${WORKSPACE_BORDER_ROOT}/src/seeds.cpp
  ${WORKSPACE_BORDER_ROOT}/src/background.cpp
  ${WORKSPACE_BORDER_ROOT}/src/similarity.cpp
  ${WORKSPACE_BORDER_ROOT}/src/grower.cpp
  ${WORKSPACE_BORDER_ROOT}/src/geometry.cpp
  ${WORKSPACE_BORDER_ROOT}/src/scoring.cpp
  ${WORKSPACE_BORDER_ROOT}/src/refine.cpp
  ${WORKSPACE_BORDER_ROOT}/src/validate.cpp
  ${WORKSPACE_BORDER_ROOT}/src/detector.cpp
  src/navigator_thumbnail.cpp
  src/canvas_observe.cpp
  src/workspace_canvas_relation.cpp
  src/viewport_frame.cpp
  src/transform_solve.cpp
  src/sct_c_api.cpp
)

target_include_directories(ScreenCanvasNative PUBLIC
  ${WORKSPACE_BORDER_ROOT}/include
  ${CMAKE_CURRENT_SOURCE_DIR}/include
)
target_compile_definitions(ScreenCanvasNative PRIVATE SCT_NATIVE_EXPORTS)

if(MSVC)
  target_compile_options(ScreenCanvasNative PRIVATE /W3 /EHsc /utf-8)
  set_property(TARGET ScreenCanvasNative PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()
