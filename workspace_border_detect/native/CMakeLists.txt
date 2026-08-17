cmake_minimum_required(VERSION 3.20)
project(WorkspaceBorderNative LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(WorkspaceBorderNative SHARED
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
  src/c_api.cpp
)

target_include_directories(WorkspaceBorderNative PUBLIC
  ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_compile_definitions(WorkspaceBorderNative PRIVATE WB_NATIVE_EXPORTS)

if(MSVC)
  target_compile_options(WorkspaceBorderNative PRIVATE /W3 /EHsc /utf-8)
  set_property(TARGET WorkspaceBorderNative PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

add_executable(wb_smoke src/smoke_main.cpp)
target_link_libraries(wb_smoke PRIVATE WorkspaceBorderNative)
target_include_directories(wb_smoke PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/include)
