"""Create a Resolve project from a StoryForge X timeline and put its markers on the ruler.

Taken over from RawCutX's tested "Send to Resolve". Run by StoryForge X through DaVinci Resolve's own interpreter (ResolvePython.exe, Resolve 21.1+), where
`import DaVinciResolveScript` resolves with no environment variables set.

Usage: ResolvePython.exe send_to_resolve.py <request.json>

Prints exactly one JSON object on stdout. Every failure path prints it too - the caller parses
stdout, so a silent exit tells it nothing. The one earlier word to the caller is the empty
`<request.json>.created` file written the moment the project exists (see signal_created).
"""

import json
import sys
import time

CONNECT_TIMEOUT_SECONDS = 180
CONNECT_POLL_SECONDS = 2.0
CREATE_RETRY_SECONDS = 30
CREATE_POLL_SECONDS = 1.0


def emit(state, code):
    sys.stdout.write(json.dumps(state))
    sys.stdout.flush()
    return code


def connect(state):
    """Retry until Resolve answers. This is what absorbs a cold start, which can take well over a
    minute - longer on a machine that has just been switched on."""
    try:
        import DaVinciResolveScript as dvr
    except Exception as error:
        state["problem"] = (
            "Could not load Resolve's scripting module (%s). This needs DaVinci Resolve Studio "
            "21.1 or newer." % error
        )
        return None

    deadline = time.monotonic() + CONNECT_TIMEOUT_SECONDS
    while True:
        try:
            resolve = dvr.scriptapp("Resolve")
        except Exception:
            resolve = None

        if resolve is not None:
            return resolve

        if time.monotonic() >= deadline:
            state["problem"] = (
                "DaVinci Resolve did not answer within %d seconds. It must be DaVinci Resolve "
                "Studio, fully started, with Preferences > System > General > External scripting "
                "set to Local." % CONNECT_TIMEOUT_SECONDS
            )
            return None

        time.sleep(CONNECT_POLL_SECONDS)


def listed_projects(manager):
    """The project names in Resolve's current folder, or None when the list cannot be read - which
    is no evidence either way, so the caller keeps retrying but never claims the name is free.
    Resolve's API mostly fails by returning None rather than raising, so None is unread too."""
    try:
        listed = manager.GetProjectListInCurrentFolder()
    except Exception:
        return None
    if listed is None:
        return None
    return [str(name) for name in listed]


def near_matches(listed, name):
    """Names equal to `name` once case and surrounding spaces are ignored, but not exactly. Whether
    Resolve counts them as the same name is unknown, so one never stops the retry - the update
    dialog may still be what refused the name - it is only named once the retry has run out."""
    wanted = name.strip().casefold()
    return [other for other in listed if other != name and other.strip().casefold() == wanted]


def create_project(manager, name, state):
    """CreateProject returns None when the name is taken, when it contains a character Resolve
    refuses - and while Resolve's Software Update window is up, which it shows on a cold start just
    as a send arrives (2026-10-28: refused for a name nobody had, created at once after the window
    was closed). The C# side closes that window as soon as it sees it, so a refusal for a free name
    is retried until it has. Never load or rename on a refusal: the user asked for a new project
    with that name, and quietly doing something else puts their cut somewhere they did not expect."""
    # monotonic, not time(): a cold start is when Windows tends to sync its clock, and a jump must
    # neither stretch this retry nor cut it short.
    deadline = time.monotonic() + CREATE_RETRY_SECONDS
    while True:
        project = manager.CreateProject(name)
        if project is not None:
            return project

        listed = listed_projects(manager)
        if listed is not None and name in listed:
            state["problem"] = (
                "There is already a project called '%s' in Resolve - nothing was created. Send "
                "again with another name." % name
            )
            return None

        if time.monotonic() >= deadline:
            near = near_matches(listed, name) if listed is not None else []
            if listed is None:
                state["problem"] = (
                    "Resolve would not create a project called '%s', and its project list could "
                    "not be read to say why. The name may already be taken, or contain a character "
                    "Resolve does not accept - or a dialog, such as its Software Update notice, is "
                    "in the way. Nothing was created; check Resolve, then send again." % name
                )
            elif near:
                state["problem"] = (
                    "Resolve would not create a project called '%s'. It already has '%s', which "
                    "differs only in case or spaces - Resolve may count that as the same name. If "
                    "Resolve is showing a dialog - its Software Update notice, say - close it and "
                    "send again; otherwise send with another name." % (name, near[0])
                )
            else:
                state["problem"] = (
                    "Resolve would not create a project called '%s', and no project by that name "
                    "exists. If Resolve is showing a dialog - its Software Update notice, say - "
                    "close it and send again; otherwise the name may contain a character Resolve "
                    "does not accept." % name
                )
            return None

        time.sleep(CREATE_POLL_SECONDS)


def describe_rate_mismatch(timeline, requested_rate, rate_accepted):
    """Compares the rate the timeline actually imported at against the one requested. SetSetting
    can report success and still have Resolve reconform the timeline on import, or it can report
    failure by returning a falsy value instead of raising - either way, this read-back after the
    import is what actually tells the user what happened.

    `requested_rate` arrives as a string ("60", "29.97"); Resolve's GetSetting hands back a float
    ("29.97" can come back as 29.97002997), so the comparison is numeric with a small tolerance,
    not exact. Guarded end to end: a GetSetting that returns None or something unparseable, or a
    requested_rate that will not parse, must not be read as a mismatch - if the actual rate can't
    be confirmed, this says nothing about a mismatch. The one thing it does still say something
    about is SetSetting itself having already reported failure - that is the specific silent
    failure this check exists to prevent, so it is not swallowed even when the read-back is
    inconclusive.
    """
    try:
        requested = float(requested_rate)
    except (TypeError, ValueError):
        requested = None

    try:
        actual = timeline.GetSetting("timelineFrameRate")
        actual = float(actual) if actual is not None else None
    except Exception:
        actual = None

    if requested is not None and actual is not None:
        if abs(actual - requested) >= 0.01:
            return (
                "The timeline imported, but Resolve set it to %g fps instead of the %g fps the "
                "video was rendered at. It looks like the rate was conformed rather than "
                "honoured - check Timeline Settings before you start cutting." % (actual, requested)
            )
        return None

    if not rate_accepted:
        return (
            "The timeline imported, but Resolve reported it could not set the frame rate to %s "
            "fps before the import, and the actual rate could not be confirmed afterwards. Check "
            "Timeline Settings before you start cutting." % requested_rate
        )

    return None


def signal_created(request_path):
    """Writes the empty `<request>.created` file that tells StoryForge X the project exists, so it stops
    closing Resolve's update windows - one the user opens during the import is theirs. Best effort:
    a missing signal only means StoryForge X keeps watching until the script exits."""
    try:
        with open(request_path + ".created", "w", encoding="utf-8"):
            pass
    except Exception:
        pass


def run(request, state, request_path):
    resolve = connect(state)
    if resolve is None:
        return 1

    manager = resolve.GetProjectManager()
    if manager is None:
        state["problem"] = "Resolve answered but returned no project manager."
        return 1

    state["stage"] = "create"
    name = request["projectName"]
    project = create_project(manager, name, state)
    if project is None:
        return 1
    signal_created(request_path)

    # Before the import, and this order matters: a new project is 24 fps, and importing a 60 fps
    # timeline into it makes Resolve conform the timeline instead of honouring it.
    state["stage"] = "rate"
    requested_rate = request["frameRate"]
    # SetSetting can return a falsy value instead of raising when Resolve rejects the rate. Do not
    # discard that and carry on as if it succeeded - capture it, and let it feed the read-back
    # check below, which is what confirms what actually happened after the import.
    rate_accepted = project.SetSetting("timelineFrameRate", requested_rate)

    state["stage"] = "import"
    pool = project.GetMediaPool()
    timeline = pool.ImportTimelineFromFile(request["timelinePath"])
    if timeline is None:
        state["problem"] = (
            "The project '%s' was created, but Resolve would not import %s. The project is still "
            "there - open or delete it in the Project Manager." % (name, request["timelinePath"])
        )
        return 1

    # Everything from here is a bonus on top of a timeline that is already safely in. Set BEFORE
    # timeline.GetName() below, not after - GetName() and GetItemListInTrack are both real API
    # calls that can raise, and if ok were set after them an exception there would report a
    # completed import as a total failure (review finding, round 2: this used to sit after both
    # reads, which closed only half the finding - GetName() was still unguarded and un-True'd). A
    # failure below reports the stage but leaves ok True, because losing the markers is not losing
    # the import - the same rule RawCutX's EDL export follows.
    state["ok"] = True

    try:
        state["timelineName"] = timeline.GetName()
    except Exception:
        state["timelineName"] = None

    # A transition is an item on the track as well, but has no media pool item behind it; counting
    # it made a cut of 11 clips with 6 cross dissolves come back as "17 clips".
    try:
        items = timeline.GetItemListInTrack("video", 1) or []
        state["videoItems"] = sum(1 for item in items if item.GetMediaPoolItem() is not None)
    except Exception:
        state["videoItems"] = 0

    # Collected rather than written straight into state["problem"]: that is a single slot, and a
    # confirmed frame-rate conform - the most consequential silent failure this script exists to
    # catch, since it means Resolve silently reconformed the user's 60 fps cut to 24 - must never be
    # clobbered by a lesser message (a marker shortfall, an Edit-page open failure) that happens to
    # run after it. Every problem below is appended here and joined into the one string the JSON
    # contract still promises at the end of run().
    problems = []

    rate_problem = describe_rate_mismatch(timeline, requested_rate, rate_accepted)
    if rate_problem is not None:
        problems.append(rate_problem)

    if request.get("placeMarkers"):
        state["stage"] = "markers"
        placed = 0
        for marker in request.get("markers", []):
            try:
                # frame is relative to the timeline's first frame, NOT to its start timecode: a
                # timeline reporting GetStartFrame() == 216000 still takes AddMarker(45) at 45.
                if timeline.AddMarker(
                    int(marker["frame"]), marker["color"], marker["text"], marker["text"], 1, ""
                ):
                    placed += 1
            except Exception:
                pass
        state["markersPlaced"] = placed
        if placed < len(request.get("markers", [])):
            problems.append(
                "The timeline imported, but only %d of %d markers could be placed. "
                "Place the rest by hand."
                % (placed, len(request.get("markers", [])))
            )

    state["stage"] = "open"
    try:
        project.SetCurrentTimeline(timeline)
        resolve.OpenPage("edit")
    except Exception as error:
        problems.append(
            "The timeline imported but Resolve would not open it on the Edit page (%s). "
            "It is in the project - open it yourself." % error
        )
        state["problem"] = " ".join(problems)
        return 0

    state["stage"] = "done"
    if problems:
        state["problem"] = " ".join(problems)
    return 0


def main():
    state = {
        "ok": False,
        "stage": "connect",
        "problem": None,
        "timelineName": None,
        "videoItems": 0,
        "markersPlaced": 0,
    }

    if len(sys.argv) < 2:
        state["problem"] = "No request file was given."
        return emit(state, 2)

    try:
        with open(sys.argv[1], "r", encoding="utf-8") as handle:
            request = json.load(handle)
    except Exception as error:
        state["problem"] = "Could not read the request file: %s" % error
        return emit(state, 2)

    try:
        code = run(request, state, sys.argv[1])
    except Exception as error:
        # Anything unforeseen still leaves the caller a sentence rather than a traceback it has to
        # guess at. The stage already says how far we got.
        #
        # Deliberately NOT state["ok"] = False: run() sets ok True the moment the import succeeds,
        # because everything after that is a bonus on top of a timeline that is already safely in
        # (see the comment above that assignment). This handler used to flip it back regardless,
        # which turned an exception in the marker loop - or in timeline.GetName() right after a
        # good import - into "the send failed", when the project and timeline were really there.
        # The import is the thing that matters: if it already succeeded, an unforeseen failure below
        # is a problem to report, not a reason to tell the user nothing happened.
        state["problem"] = "The Resolve script failed at stage '%s': %s" % (state["stage"], error)
        return emit(state, 0 if state["ok"] else 1)

    return emit(state, code)


if __name__ == "__main__":
    sys.exit(main())
