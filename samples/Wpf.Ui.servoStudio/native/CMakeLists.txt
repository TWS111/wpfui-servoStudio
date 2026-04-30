cmake_minimum_required(VERSION 3.12)
project(soemfoe C)

# ── Build SOEM shared library with FoE support ──

set(SOEM_DIR "${CMAKE_CURRENT_SOURCE_DIR}/SOEM" CACHE PATH "Path to SOEM source")

if(NOT EXISTS "${SOEM_DIR}/include/soem/soem.h")
    message(FATAL_ERROR "SOEM source not found at ${SOEM_DIR}. Run build_soemfoe.ps1 to auto-clone.")
endif()

# ── SOEM configurable options (must match SOEM's own CMakeLists.txt) ──
set(EC_BUFSIZE EC_MAXECATFRAME)
set(EC_MAXBUF 16)
set(EC_MAXEEPBITMAP 128)
set(EC_MAXEEPBUF "EC_MAXEEPBITMAP << 5")
set(EC_LOGGROUPOFFSET 16)
set(EC_MAXELIST 64)
set(EC_MAXNAME 40)
set(EC_MAXSLAVE 200)
set(EC_MAXGROUP 2)
set(EC_MAXIOSEGMENTS 64)
set(EC_MAXMBX 1486)
set(EC_MBXPOOLSIZE 32)
set(EC_MAXEEPDO 0x200)
set(EC_MAXSM 8)
set(EC_MAXFMMU 4)
set(EC_MAXLEN_ADAPTERNAME 128)
set(EC_MAX_MAPT 1)
set(EC_MAXODLIST 1024)
set(EC_MAXOELIST 256)
set(EC_SOE_MAXNAME 60)
set(EC_SOE_MAXMAPPING 64)
set(EC_TIMEOUTRET 2000)
set(EC_TIMEOUTRET3 "EC_TIMEOUTRET * 3")
set(EC_TIMEOUTSAFE 20000)
set(EC_TIMEOUTEEP 20000)
set(EC_TIMEOUTTXM 20000)
set(EC_TIMEOUTRXM 700000)
set(EC_TIMEOUTSTATE 2000000)
set(EC_DEFAULTRETRIES 3)
set(EC_PRIMARY_MAC_ARRAY "{0x0101, 0x0101, 0x0101}")
set(EC_SECONDARY_MAC_ARRAY "{0x0404, 0x0404, 0x0404}")

# Generate ec_options.h from template
configure_file(
    ${SOEM_DIR}/include/soem/ec_options.h.in
    ${CMAKE_CURRENT_BINARY_DIR}/include/soem/ec_options.h
)

# SOEM core source files (includes ec_foe.c!)
set(SOEM_SOURCES
    ${SOEM_DIR}/src/ec_base.c
    ${SOEM_DIR}/src/ec_coe.c
    ${SOEM_DIR}/src/ec_config.c
    ${SOEM_DIR}/src/ec_dc.c
    ${SOEM_DIR}/src/ec_foe.c
    ${SOEM_DIR}/src/ec_main.c
    ${SOEM_DIR}/src/ec_print.c
    ${SOEM_DIR}/src/ec_soe.c
    ${SOEM_DIR}/src/ec_eoe.c
)

# Platform-specific OSAL + NIC layer
if(WIN32)
    set(SOEM_SOURCES ${SOEM_SOURCES}
        ${SOEM_DIR}/osal/win32/osal.c
        ${SOEM_DIR}/oshw/win32/nicdrv.c
        ${SOEM_DIR}/oshw/win32/oshw.c
    )
    set(SOEM_INCLUDES
        ${SOEM_DIR}/include
        ${CMAKE_CURRENT_BINARY_DIR}/include
        ${SOEM_DIR}/osal
        ${SOEM_DIR}/osal/win32
        ${SOEM_DIR}/oshw/win32
        ${SOEM_DIR}/oshw/win32/wpcap/Include
    )
else()
    set(SOEM_SOURCES ${SOEM_SOURCES}
        ${SOEM_DIR}/osal/linux/osal.c
        ${SOEM_DIR}/oshw/linux/nicdrv.c
        ${SOEM_DIR}/oshw/linux/oshw.c
    )
    set(SOEM_INCLUDES
        ${SOEM_DIR}/include
        ${CMAKE_CURRENT_BINARY_DIR}/include
        ${SOEM_DIR}/osal
        ${SOEM_DIR}/osal/linux
        ${SOEM_DIR}/oshw/linux
    )
endif()

add_library(soemfoe SHARED
    ${SOEM_SOURCES}
    ${CMAKE_CURRENT_SOURCE_DIR}/soemfoe_wrapper.c
)

target_include_directories(soemfoe PRIVATE ${SOEM_INCLUDES})

if(WIN32)
    if(CMAKE_SIZEOF_VOID_P EQUAL 8)
        target_link_directories(soemfoe PRIVATE "${SOEM_DIR}/oshw/win32/wpcap/Lib/x64")
    else()
        target_link_directories(soemfoe PRIVATE "${SOEM_DIR}/oshw/win32/wpcap/Lib")
    endif()
    target_link_libraries(soemfoe PRIVATE wpcap Packet ws2_32 winmm)
    target_compile_definitions(soemfoe PRIVATE _CRT_SECURE_NO_WARNINGS WIN32_LEAN_AND_MEAN)
endif()

if(MSVC)
    set_target_properties(soemfoe PROPERTIES WINDOWS_EXPORT_ALL_SYMBOLS ON)
endif()

set_target_properties(soemfoe PROPERTIES
    OUTPUT_NAME "soemfoe"
    PREFIX ""
)
