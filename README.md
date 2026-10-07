# StoryForge X

A Windows desktop app that turns a brief into a narrated video: research, script, voice,
storyboard, reference images, stills, clips and assembly, each stage reviewable in a result
matrix. All models run locally (Claude CLI or LM Studio for text, ComfyUI for voice, images and
video, FFmpeg or DaVinci Resolve for assembly).

## Research

Research reads only the sources you name for a project. Claude CLI runs it with no tools of its
own: the only way to the web is `StoryForge.ResearchServer`, a small MCP server with `search` and
`fetch` that refuses every address outside the project's sources. Wikis (bg3.wiki, Fandom) are
searched and read through their MediaWiki API. Every fact carries its source page and the passage
it rests on, and the engine checks that the page was really read and the passage is really on it
before you see the fact sheet. There you weigh each fact from 1 (only if there is time) to 10 (must
be in the video), leave facts out, reword them, and approve.

## Plan

The work is planned as GitHub issues in build order: see the
[v1 milestone](https://github.com/Little-God1983/StoryForgeX/milestone/1).

## Stable build

Double-click `scripts\build.cmd` on `main` to build, test and install the next version in
`E:\StableVersion\StoryForgeX`, with a `StoryForge X` Start Menu entry. See
[scripts/README.md](scripts/README.md).
