cmake_minimum_required(VERSION 3.18.1)
project(audiodsp LANGUAGES C)

add_library(audiodsp SHARED
    audio_dsp.c
)

target_link_libraries(audiodsp
    m
)
