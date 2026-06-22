set(CMAKE_CROSSCOMPILING TRUE)
set(CMAKE_SYSTEM_NAME Generic)
set(CMAKE_SYSTEM_PROCESSOR powerpc)

get_filename_component(BSE_ROOT "${CMAKE_CURRENT_LIST_DIR}/../lib/BetterSunshineEngine" ABSOLUTE)

if(NOT EXISTS "${BSE_ROOT}/compiler/clang.exe")
    message(FATAL_ERROR "BSE clang not found at ${BSE_ROOT}/compiler/clang.exe — run: git lfs pull in BetterSunshineEngine")
endif()

set(CMAKE_C_COMPILER "${BSE_ROOT}/compiler/clang.exe" CACHE FILEPATH "Kuribo clang" FORCE)
set(CMAKE_CXX_COMPILER "${BSE_ROOT}/compiler/clang.exe" CACHE FILEPATH "Kuribo clang" FORCE)
set(CMAKE_C_LINK_EXECUTABLE "${BSE_ROOT}/compiler/clang.exe")

set(triple powerpc-gecko-ibm-kuribo-eabi)
set(CMAKE_C_COMPILER_TARGET ${triple})
set(CMAKE_CXX_COMPILER_TARGET ${triple})

if(WIN32)
    set(CMAKE_LIBRARY_PATH "C:/Windows/System32")
endif()

set(CMAKE_SYSROOT "C:/msys64/mingw64")

set(CMAKE_EXE_LINKER_FLAGS_INIT "-fuse-ld=lld -T ${BSE_ROOT}/linker.ld")
set(CMAKE_MODULE_LINKER_FLAGS_INIT "-fuse-ld=lld -T ${BSE_ROOT}/linker.ld")
set(CMAKE_SHARED_LINKER_FLAGS_INIT "-fuse-ld=lld -T ${BSE_ROOT}/linker.ld")

set(CMAKE_CXX_STANDARD_LIBRARIES "")

if(NOT SMS_REGION)
    set(SMS_REGION us)
endif()

set(SMS_COMPILE_DEFINES
    -D__powerpc__ -DKURIBO_NO_TYPES -DNTSCU
    -DGEKKO -DNDEBUG
)

set(SMS_COMPILE_FLAGS
    $<$<COMPILE_LANGUAGE:CXX>:-std=gnu++17>
    --target=${CMAKE_CXX_COMPILER_TARGET}
    ${SMS_COMPILE_DEFINES}
    -Os -fno-exceptions
    -fno-rtti -ffast-math -fpermissive
    -fdeclspec -fno-unwind-tables
    -nodefaultlibs -nobuiltininc -nostdinc++ -nostdlib
    -fno-use-init-array -fno-use-cxa-atexit
    -fno-c++-static-destructors -fno-function-sections
    -fno-data-sections -fpermissive
    -Werror -Wno-main
    -Wno-incompatible-library-redeclaration
)

set(SMS_LINK_FLAGS
    $<$<COMPILE_LANGUAGE:CXX>:-std=gnu++17>
    --target=${CMAKE_CXX_COMPILER_TARGET}
    -r -v -fuse-ld=lld
    -fdeclspec -fno-exceptions -fno-rtti
    -fno-unwind-tables -ffast-math
    -nodefaultlibs -nostdlib -fno-use-init-array
    -fno-use-cxa-atexit -fno-c++-static-destructors
    -fno-function-sections -fno-data-sections
    -fpermissive -Werror
)

set(CMAKE_C_COMPILER_FORCED TRUE)
set(CMAKE_CXX_COMPILER_FORCED TRUE)

set(CMAKE_OBJCOPY "${BSE_ROOT}/compiler/powerpc-eabi-objcopy.exe" CACHE PATH "" FORCE)
