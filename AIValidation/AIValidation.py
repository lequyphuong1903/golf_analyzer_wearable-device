import argparse
import json
from pathlib import Path
import numpy as np
import torch
import torch.nn.functional as F
from torch.utils.data import DataLoader

from dataset import _load_csv_first_n_cols, MultiSensorTimeSeries, collate_batch
from model import Conv1dAutoEncoder


def load_model(ckpt_path: str):
    payload = torch.load(ckpt_path, map_location="cpu")
    cfg = payload["config"]
    model = Conv1dAutoEncoder(cfg["in_channels"], cfg["seq_len"], emb_dim=cfg.get("emb_dim", 128))
    model.load_state_dict(payload["state_dict"])
    model.eval()
    return model, cfg


def make_sample_tensor(belt: str | None, coxa: str | None, glove: str | None,
                       seq_len: int = 700, n_cols: int = 12, normalize: bool = True) -> torch.Tensor:
    import numpy as np

    arrays = []
    for p in [belt, coxa, glove]:
        if p and Path(p).exists():
            arr = _load_csv_first_n_cols(str(p), n_cols=n_cols, target_len=seq_len, normalize=normalize)  # [L, 12]
        else:
            arr = np.zeros((seq_len, n_cols), dtype=np.float32)
        arrays.append(arr)
    stacked = np.concatenate(arrays, axis=1)  # [L, 36]
    x = torch.from_numpy(stacked.T.copy()).unsqueeze(0)  # [1, 36, L]
    return x


def _latest(paths: list[Path]) -> Path | None:
    return max(paths, key=lambda p: p.stat().st_mtime) if paths else None


def _find_solution_root(start: Path) -> Path:
    # Try to find a folder that contains both AIValidation/ and GolfAnalyzer/
    for p in [start] + list(start.parents):
        if (p / "AIValidation").exists() and (p / "GolfAnalyzer").exists():
            return p
    return start.parent


def autoguess_csvs(
    belt: str | None,
    coxa: str | None,
    glove: str | None,
    artifacts: str,
) -> tuple[str | None, str | None, str | None]:
    """
    Auto find sensor1/2/3.csv with the following priority:
    1) artifacts/ (if exists)
    2) <SolutionRoot>/GolfAnalyzer/bin/Debug/**/ (net8.*, net8.*-windows)
    3) <SolutionRoot>/GolfAnalyzer/bin/**/
    Fallback to provided values or None.
    """
    start = Path(__file__).resolve()
    sol = _find_solution_root(start)

    candidate_roots: list[Path] = []

    # 1) artifacts/
    art = Path(artifacts)
    if art.exists():
        candidate_roots.append(art)

    # 2) GolfAnalyzer/bin/Debug/**/
    ga_bin = sol / "GolfAnalyzer" / "bin"
    debug_root = ga_bin / "Debug"
    if debug_root.exists():
        candidate_roots.append(debug_root)

    # 3) GolfAnalyzer/bin/**/
    if ga_bin.exists():
        candidate_roots.append(ga_bin)

    # Last resort: solution root (bounded search)
    candidate_roots.append(sol)

    def find_one(name: str) -> str | None:
        matches: list[Path] = []
        for root in candidate_roots:
            if root.exists() and root.is_dir():
                # Limit breadth to searching only a few relevant layers
                # Debug/net*/... will be covered by rglob
                matches.extend(root.rglob(name))
        latest = _latest(matches)
        return str(latest) if latest else None

    belt = belt if belt and Path(belt).exists() else (find_one("sensor2.csv") or belt)
    coxa = coxa if coxa and Path(coxa).exists() else (find_one("sensor3.csv") or coxa)
    glove = glove if glove and Path(glove).exists() else (find_one("sensor1.csv") or glove)
    return belt, coxa, glove


def run_validation(
    belt: str | None,
    coxa: str | None,
    glove: str | None,
    ckpt: str = "artifacts/models/autoencoder_3sensor_best.pt",
    artifacts: str = "artifacts",
    ref_split: str = "split_test.json",
    topk: int = 5,
    min_pct: float = 60.0,
    min_cos: float | None = None,
) -> None:
    # Compute threshold early so we can still report it on errors
    thr_cos = float(min_cos if min_cos is not None else (min_pct / 100.0) * 2.0 - 1.0)

    try:
        # Load model
        model, cfg = load_model(ckpt)
        device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
        model.to(device)

        # Resolve inputs (will use zeros if any file missing)
        belt, coxa, glove = autoguess_csvs(belt, coxa, glove, artifacts)
        print("Input CSVs:")
        print(f"  belt : {belt or '(not found, using zeros)'}")
        print(f"  coxa : {coxa or '(not found, using zeros)'}")
        print(f"  glove: {glove or '(not found, using zeros)'}")
        if not any([belt, coxa, glove]):
            print("Warning: No input CSVs found. Proceeding with zero-filled inputs; results may be meaningless.")

        # Chuẩn bị mẫu đầu vào từ 3 file CSV
        xq = make_sample_tensor(belt, coxa, glove, seq_len=cfg["seq_len"], n_cols=12, normalize=True).to(device)
        with torch.no_grad():
            _, zq = model(xq)  # [1, D]
        zq = zq[0]  # [D]

        # Tải tập tham chiếu và tính similarity
        ref_ds = MultiSensorTimeSeries(artifacts, ref_split)
        if len(ref_ds) == 0:
            msg = "empty_reference_dataset"
            print(f"Warning: {msg}.")
            summary = {
                "best_key": None,
                "best_cos": -1.0,
                "best_pct": 0.0,
                "threshold_cos": thr_cos,
                "decision": "ERROR",
                "error": msg,
            }
            print("__AIRESULT__" + json.dumps(summary, ensure_ascii=False))
            return

        loader = DataLoader(ref_ds, batch_size=128, shuffle=False, num_workers=0, collate_fn=collate_batch)

        all_keys, all_sims = [], []
        with torch.no_grad():
            for xb, keys in loader:
                xb = xb.to(device)
                _, z = model(xb)  # [B, D]
                sims = F.cosine_similarity(z, zq.unsqueeze(0).expand_as(z), dim=1)  # [B]
                all_keys.extend(keys)
                all_sims.extend(sims.cpu().tolist())

        # Top-K
        order = np.argsort(all_sims)[::-1]
        topk_idx = order[: topk]
        best_idx = topk_idx[0]
        best_cos = float(all_sims[best_idx])
        best_pct = (best_cos + 1.0) / 2.0 * 100.0

        decision = "PASS" if best_cos >= thr_cos else "FAIL"

        print(f"Most similar session: {all_keys[best_idx]}")
        print(f"Cosine similarity: {best_cos:.4f} -> {best_pct:.2f}% giống nhau")
        print(f"Decision: {decision} (threshold cosine={thr_cos:.4f}, ~{((thr_cos+1)/2*100):.1f}%)")

        print("\nTop similar sessions (đã lọc theo ngưỡng):")
        for i in topk_idx:
            cos = float(all_sims[i])
            pct = (cos + 1.0) / 2.0 * 100.0
            if cos >= thr_cos:
                print(f"- {all_keys[i]} | cosine={cos:.4f} | {pct:.2f}%")

        # Always emit a tagged JSON summary for the C# app
        summary = {
            "best_key": all_keys[best_idx],
            "best_cos": best_cos,
            "best_pct": best_pct,
            "threshold_cos": thr_cos,
            "decision": decision,
        }
        print("__AIRESULT__" + json.dumps(summary, ensure_ascii=False))

    except Exception as e:
        # Never leave C# without a result
        summary = {
            "best_key": None,
            "best_cos": -1.0,
            "best_pct": 0.0,
            "threshold_cos": thr_cos,
            "decision": "ERROR",
            "error": f"{type(e).__name__}: {e}",
        }
        print("__AIRESULT__" + json.dumps(summary, ensure_ascii=False))


def main():
    ap = argparse.ArgumentParser(description="Đánh giá % giống nhau của một lần swing (3 sensor) so với tập tham chiếu.")
    ap.add_argument("--belt", default=None, help="Path tới sensor2.csv (để trống sẽ tự tìm)")
    ap.add_argument("--coxa", default=None, help="Path tới sensor3.csv (để trống sẽ tự tìm)")
    ap.add_argument("--glove", default=None, help="Path tới sensor1.csv (để trống sẽ tự tìm)")
    ap.add_argument("--ckpt", default="artifacts/models/autoencoder_3sensor_best.pt", help="Checkpoint model 3-sensor")
    ap.add_argument("--artifacts", default="artifacts", help="Thư mục artifacts chứa sessions/split/config")
    ap.add_argument("--ref-split", default="split_test.json", choices=["split_train.json", "split_val.json", "split_test.json"],
                    help="Chọn tập tham chiếu để so sánh")
    ap.add_argument("--topk", type=int, default=5, help="Số phiên tương tự nhất để hiển thị")
    ap.add_argument("--min-pct", type=float, default=60.0, help="Ngưỡng % để kết luận giống")
    ap.add_argument("--min-cos", type=float, default=None, help="Ngưỡng cosine [-1,1]; nếu đặt thì bỏ qua --min-pct")
    args = ap.parse_args([])  # Force defaults when launched with F5

    run_validation(
        belt=args.belt,
        coxa=args.coxa,
        glove=args.glove,
        ckpt=args.ckpt,
        artifacts=args.artifacts,
        ref_split=args.ref_split,
        topk=args.topk,
        min_pct=args.min_pct,
        min_cos=args.min_cos,
    )


if __name__ == "__main__":
    main()