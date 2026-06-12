cmake_minimum_required(VERSION 3.15)
project(WheelFitmentAPI)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# ScriptHookV SDK location (adjust this path)
set(SCRIPTHOOKV_SDK "${CMAKE_CURRENT_SOURCE_DIR}/../ScriptHookV_SDK")

# Check if SDK exists
if(NOT EXISTS "${SCRIPTHOOKV_SDK}/inc/main.h")
    message(FATAL_ERROR
        "ScriptHookV SDK not found at ${SCRIPTHOOKV_SDK}\n"
        "Please download from http://www.dev-c.com/gtav/scripthookv/ and extract to:\n"
        "${SCRIPTHOOKV_SDK}"
    )
endif()

# Source files
set(SOURCES
    WheelFitmentAPI.cpp
)

set(HEADERS
    WheelFitmentAPI.h
)

# Create the DLL
add_library(WheelFitmentAPI SHARED ${SOURCES} ${HEADERS})

# Include directories
target_include_directories(WheelFitmentAPI PRIVATE
    ${SCRIPTHOOKV_SDK}/inc
)

# Link libraries
target_link_libraries(WheelFitmentAPI PRIVATE
    ${SCRIPTHOOKV_SDK}/lib/ScriptHookV.lib
    psapi
)

# Set output extension to .asi
set_target_properties(WheelFitmentAPI PROPERTIES
    SUFFIX ".asi"
    OUTPUT_NAME "WheelFitmentAPI"
)

# Windows-specific settings
if(MSVC)
    target_compile_definitions(WheelFitmentAPI PRIVATE
        _CRT_SECURE_NO_WARNINGS
        WIN32_LEAN_AND_MEAN
    )

    # Release optimizations
    target_compile_options(WheelFitmentAPI PRIVATE
        $<$<CONFIG:Release>:/O2 /Oi /GL>
    )
    target_link_options(WheelFitmentAPI PRIVATE
        $<$<CONFIG:Release>:/LTCG>
    )
endif()

# Installation
install(TARGETS WheelFitmentAPI
    RUNTIME DESTINATION scripts
)
