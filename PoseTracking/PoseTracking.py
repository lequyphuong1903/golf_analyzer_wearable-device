import json
import sys
import os
from pathlib import Path

import cv2
import mediapipe as mp


def resolve_video_path(project_name: str = "GolfAnalyzer",
                       config: str = "Release",
                       video_filename: str = "video1.avi") -> Path:
    # 1) If the host (e.g., .NET app) provides the output directory via env var, use it
    env_output_dir = os.environ.get("APP_OUTPUT_DIR")
    if env_output_dir:
        p = Path(env_output_dir) / video_filename
        if p.exists():
            return p
        first_avi = next(Path(env_output_dir).glob("*.avi"), None)
        if first_avi:
            return first_avi

    # 2) Resolve relative to the solution structure:
    #    (solution root)/GolfAnalyzer/bin/Debug/(net...)/video_filename
    script_dir = Path(__file__).resolve().parent
    solution_root = script_dir.parent  # assuming PoseTracking is alongside GolfAnalyzer
    output_base = solution_root / project_name / "bin" / config

    if output_base.exists():
        # Prefer framework-specific subfolders (e.g., net8.0-windows)
        tfm_dirs = [d for d in output_base.iterdir() if d.is_dir()]
        # Check TFM dirs first, then the base folder
        search_dirs = tfm_dirs + [output_base]
        for d in search_dirs:
            candidate = d / video_filename
            if candidate.exists():
                return candidate
            first_avi = next(d.glob("*.avi"), None)
            if first_avi:
                return first_avi

    # 3) Fallback to current working directory
    cwd_candidate = Path.cwd() / video_filename
    if cwd_candidate.exists():
        return cwd_candidate

    # 4) Last resort: return the intended name in CWD (may not exist)
    return Path(video_filename)


def process_video(input_path: str = "video1.avi",
                  output_path: str = "outputvideo.avi",
                  skeleton_only: bool = False) -> None:
    cap = cv2.VideoCapture(input_path)
    if not cap.isOpened():
        print(f"Failed to open input video: {input_path}")
        sys.exit(1)

    # Read first frame to initialize writer with proper size.
    ok, first_frame = cap.read()
    if not ok:
        print("Failed to read the first frame.")
        cap.release()
        sys.exit(1)

    height, width = first_frame.shape[:2]
    fps = cap.get(cv2.CAP_PROP_FPS)
    if fps is None or fps <= 1e-2:
        fps = 30.0  # fallback if metadata missing

    # Prefer XVID for .avi; fallback to MJPG if needed.
    def create_writer(codec: str):
        fourcc = cv2.VideoWriter_fourcc(*codec)
        return cv2.VideoWriter(output_path, fourcc, fps, (width, height))

    writer = create_writer("XVID")
    if not writer.isOpened():
        writer = create_writer("MJPG")
    if not writer.isOpened():
        print(f"Failed to create video writer for: {output_path}")
        cap.release()
        sys.exit(1)

    mp_pose = mp.solutions.pose
    mp_drawing = mp.solutions.drawing_utils

    with mp_pose.Pose(
        static_image_mode=False,
        model_complexity=1,
        enable_segmentation=False,
        min_detection_confidence=0.5,
        min_tracking_confidence=0.5
        # Note: omit refine_landmarks for compatibility with older MediaPipe versions
    ) as pose:

        # Process already-read first frame
        frames_processed = 0
        for frame in [first_frame]:
            output = _process_frame(frame, pose, mp_drawing, mp_pose, skeleton_only)
            writer.write(output)
            frames_processed += 1

        # Process remaining frames
        while True:
            ok, frame = cap.read()
            if not ok:
                break
            output = _process_frame(frame, pose, mp_drawing, mp_pose, skeleton_only)
            writer.write(output)
            frames_processed += 1

    cap.release()
    writer.release()
    print(f"Done. Frames written: {frames_processed}. Output: {Path(output_path).resolve()}")


def _process_frame(frame, pose, mp_drawing, mp_pose, skeleton_only: bool):
    # Convert BGR to RGB for MediaPipe
    rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
    rgb.flags.writeable = False
    results = pose.process(rgb)
    rgb.flags.writeable = True

    if skeleton_only:
        canvas = (0 * frame).copy()  # black background
    else:
        canvas = frame.copy()

    if not results.pose_landmarks:
        return canvas

    h, w = canvas.shape[:2]
    lm = results.pose_landmarks.landmark

    # Allowed simple joints (exclude eyes and mouth)
    PL = mp_pose.PoseLandmark
    allowed_points = {
        PL.NOSE,
        PL.LEFT_EAR, PL.RIGHT_EAR,
        PL.LEFT_SHOULDER, PL.RIGHT_SHOULDER,
        PL.LEFT_ELBOW, PL.RIGHT_ELBOW,
        PL.LEFT_WRIST, PL.RIGHT_WRIST,
        PL.LEFT_HIP, PL.RIGHT_HIP,
        PL.LEFT_KNEE, PL.RIGHT_KNEE,
        PL.LEFT_ANKLE, PL.RIGHT_ANKLE,
        PL.LEFT_HEEL, PL.RIGHT_HEEL,
        PL.LEFT_FOOT_INDEX, PL.RIGHT_FOOT_INDEX,
    }

    # Custom minimal connections (head, torso, arms, legs, feet)
    allowed_connections = [
        # Head (no eyes): connect nose to ears
        (PL.NOSE, PL.LEFT_EAR),
        (PL.NOSE, PL.RIGHT_EAR),

        # Torso
        (PL.LEFT_SHOULDER, PL.RIGHT_SHOULDER),
        (PL.LEFT_HIP, PL.RIGHT_HIP),
        (PL.LEFT_SHOULDER, PL.LEFT_HIP),
        (PL.RIGHT_SHOULDER, PL.RIGHT_HIP),

        # Arms
        (PL.LEFT_SHOULDER, PL.LEFT_ELBOW),
        (PL.LEFT_ELBOW, PL.LEFT_WRIST),
        (PL.RIGHT_SHOULDER, PL.RIGHT_ELBOW),
        (PL.RIGHT_ELBOW, PL.RIGHT_WRIST),

        # Legs
        (PL.LEFT_HIP, PL.LEFT_KNEE),
        (PL.LEFT_KNEE, PL.LEFT_ANKLE),
        (PL.RIGHT_HIP, PL.RIGHT_KNEE),
        (PL.RIGHT_KNEE, PL.RIGHT_ANKLE),

        # Feet
        (PL.LEFT_ANKLE, PL.LEFT_HEEL),
        (PL.LEFT_HEEL, PL.LEFT_FOOT_INDEX),
        (PL.RIGHT_ANKLE, PL.RIGHT_HEEL),
        (PL.RIGHT_HEEL, PL.RIGHT_FOOT_INDEX),
    ]
    allowed_connections = [(int(a.value), int(b.value)) for a, b in allowed_connections]

    # Draw only the allowed connections; suppress automatic landmark dots
    mp_drawing.draw_landmarks(
        canvas,
        results.pose_landmarks,
        allowed_connections,
        landmark_drawing_spec=mp_drawing.DrawingSpec(color=(0, 0, 0), thickness=0, circle_radius=0),
        connection_drawing_spec=mp_drawing.DrawingSpec(color=(0, 255, 0), thickness=2, circle_radius=2),
    )

    # Draw only allowed landmark points manually
    for pl in allowed_points:
        idx = int(pl.value)
        pt = lm[idx]
        # Optionally gate by visibility to avoid noisy points
        if getattr(pt, "visibility", 1.0) < 0.5:
            continue
        cx, cy = int(pt.x * w), int(pt.y * h)
        cv2.circle(canvas, (cx, cy), 3, (0, 200, 255), thickness=-1, lineType=cv2.LINE_AA)

    return canvas


if __name__ == "__main__":
    # Usage: python PoseTracking.py [skeleton_only: 0|1]
    sk_only = bool(int(sys.argv[1])) if len(sys.argv) > 1 else False

    # Video 1
    in1_path = resolve_video_path(project_name="GolfAnalyzer", config="Release", video_filename="video1.avi")
    out1_path = (in1_path.parent if in1_path.parent.exists() else Path.cwd()) / "outputvideo1.avi"

    # Video 2
    in2_path = resolve_video_path(project_name="GolfAnalyzer", config="Release", video_filename="video2.avi")
    out2_path = (in2_path.parent if in2_path.parent.exists() else Path.cwd()) / "outputvideo2.avi"

    # Process sequentially
    process_video(str(in1_path), str(out1_path), sk_only)
    process_video(str(in2_path), str(out2_path), sk_only)