cmake_minimum_required(VERSION 3.16)

project(SMSO VERSION 1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT SMS_REGION)
    set(SMS_REGION "us")
endif()

option(SMSO_REMOTE_ENEMY_MARIO "Enable remote Mario model bodies" ON)
if(SMSO_REMOTE_ENEMY_MARIO)
    add_compile_definitions(SMSO_REMOTE_ENEMY_MARIO)
endif()

set(SMSO_SOURCES
    src/comm_buffer.cpp
    src/puppets.cpp
    src/nametag_system.cpp
    src/connection_hud.cpp
    src/remote_mario.cpp
    src/remote_actor.cpp
    src/remote_mario_audio.cpp
    src/voice_sync.cpp
    src/remote_water_sync.cpp
    src/blooper_surf_sync.cpp
    src/hide_seek.cpp
    src/stage_guard.cpp
    src/cutscene_skip.cpp
    src/module.cpp
    src/collectible_scan.cpp
    src/coin_collect_fx.cpp
    src/world_sync.cpp
    src/red_coin_sync.cpp
    src/yoshi_sync.cpp
)

add_executable(_SMSO ${SMSO_SOURCES})
target_include_directories(_SMSO PUBLIC include)

if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/lib/BetterSunshineEngine/CMakeLists.txt")
    add_subdirectory(lib/BetterSunshineEngine)
    target_link_libraries(_SMSO PUBLIC BetterSunshineEngine)
    target_compile_options(_SMSO PUBLIC ${SMS_COMPILE_FLAGS})
    target_link_options(_SMSO PUBLIC ${SMS_LINK_FLAGS})
    target_compile_definitions(_SMSO PUBLIC "KURIBO_NO_TYPES")
    target_include_directories(_SMSO PUBLIC include)

    set(KURIBO_CONVERTER "${CMAKE_CURRENT_SOURCE_DIR}/lib/BetterSunshineEngine/tools/KuriboConverter.exe")
    set(SMS_MAP "${CMAKE_CURRENT_SOURCE_DIR}/lib/BetterSunshineEngine/maps/${SMS_REGION}.map")

    add_custom_target(_SMSO.kxe ALL DEPENDS _SMSO)
    add_custom_command(
        TARGET _SMSO.kxe
        POST_BUILD
        COMMAND ${KURIBO_CONVERTER} ARGS _SMSO _SMSO.kxe ${SMS_MAP}
        WORKING_DIRECTORY ${CMAKE_BINARY_DIR}
        COMMENT "Converting _SMSO to _SMSO.kxe"
    )
else()
    message(WARNING "BSE submodule missing — run: git submodule update --init --recursive")
endif()
