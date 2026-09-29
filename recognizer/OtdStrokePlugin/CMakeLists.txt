cmake_minimum_required(VERSION 3.24)
project(OtdStrokePlugin VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

option(OTD_STROKE_BUILD_TESTS "Build unit tests" ON)
option(OTD_STROKE_BUILD_SHARED "Build shared library (DLL) with C ABI" ON)

set(OTD_STROKE_SOURCES
    src/io/async_record_queue.cpp
    src/io/binary_encoder.cpp
    src/io/binary_writer.cpp
    src/recorder/stroke_recorder.cpp
    src/reader/binary_reader.cpp
    src/exporter/json_exporter.cpp
    src/plugin/plugin_entry.cpp
)

# Internal static library used by tools/tests (full C++ API).
add_library(otd_stroke_core STATIC ${OTD_STROKE_SOURCES})
target_compile_definitions(otd_stroke_core PUBLIC OTD_STROKE_STATIC)
target_include_directories(otd_stroke_core
    PUBLIC
        "${CMAKE_CURRENT_SOURCE_DIR}/include"
    PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/src"
)

if(MSVC)
    target_compile_options(otd_stroke_core PRIVATE /W4 /permissive- /utf-8)
else()
    target_compile_options(otd_stroke_core PRIVATE -Wall -Wextra -Wpedantic)
endif()

# Shared library for OpenTabletDriver / PInvoke consumers.
if(OTD_STROKE_BUILD_SHARED)
    add_library(otd_stroke SHARED ${OTD_STROKE_SOURCES})
    target_compile_definitions(otd_stroke PRIVATE OTD_STROKE_EXPORTS)
    target_include_directories(otd_stroke
        PUBLIC
            "${CMAKE_CURRENT_SOURCE_DIR}/include"
        PRIVATE
            "${CMAKE_CURRENT_SOURCE_DIR}/src"
    )
    if(MSVC)
        target_compile_options(otd_stroke PRIVATE /W4 /permissive- /utf-8)
    endif()
    add_library(OtdStrokePlugin::otd_stroke ALIAS otd_stroke)
else()
    add_library(OtdStrokePlugin::otd_stroke ALIAS otd_stroke_core)
endif()

add_executable(otd_stroke_demo src/tools/demo_main.cpp)
target_link_libraries(otd_stroke_demo PRIVATE otd_stroke_core)
target_include_directories(otd_stroke_demo PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src")

add_executable(otd_stroke_export src/tools/export_main.cpp)
target_link_libraries(otd_stroke_export PRIVATE otd_stroke_core)
if(WIN32)
    target_link_libraries(otd_stroke_export PRIVATE shell32)
endif()

if(OTD_STROKE_BUILD_TESTS)
    enable_testing()
    add_executable(otd_stroke_recorder_test tests/recorder_test.cpp)
    target_link_libraries(otd_stroke_recorder_test PRIVATE otd_stroke_core)
    target_include_directories(otd_stroke_recorder_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src")
    add_test(NAME otd_stroke_recorder_test COMMAND otd_stroke_recorder_test)
endif()
