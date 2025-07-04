cmake_minimum_required(VERSION 3.15)
project(rnnoise C)

# Enforce using the DLL CRT (/MD) on MSVC/Clang-CL to ensure a single shared runtime
if (MSVC OR CMAKE_C_COMPILER_ID STREQUAL "Clang")
  # For CMake >= 3.15
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreadedDLL" CACHE STRING "" FORCE)
  # Fallback for older CMake versions:
  # set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} /MD")
endif()

# Enable M_PI in <math.h> on Windows/MSVC/Clang-CL
if (MSVC OR CMAKE_C_COMPILER_ID STREQUAL "Clang")
  add_compile_definitions(_USE_MATH_DEFINES)
endif()

# Build a shared library by default
option(BUILD_SHARED_LIBS "Produce a shared library" ON)

# Gather all rnnoise sources
file(GLOB_RECURSE RNNOISE_SOURCES
    "${CMAKE_CURRENT_SOURCE_DIR}/src/*.c"
)

# Define the DLL
add_library(rnnoise SHARED ${RNNOISE_SOURCES})

# Auto-export every public symbol so the import-lib is complete
set_target_properties(rnnoise PROPERTIES
  WINDOWS_EXPORT_ALL_SYMBOLS ON
)

# Include the public headers
target_include_directories(rnnoise PUBLIC
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
)

# Keep the RNNOISE_EXPORTS define for any manual __declspec(dllexport) macros
# Also define RNNOISE_BUILD and DLL_EXPORT so RNNOISE_EXPORT correctly expands
if (WIN32)
  target_compile_definitions(rnnoise PRIVATE
    RNNOISE_EXPORTS
    RNNOISE_BUILD
    DLL_EXPORT
  )
endif()
