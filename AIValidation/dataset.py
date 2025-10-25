from pathlib import Path
import json
from typing import Dict, List, Tuple
import numpy as np
import pandas as pd
import torch
from torch.utils.data import Dataset
import re

def _resample_to_len(arr: np.ndarray, target_len: int) -> np.ndarray:
    T, C = arr.shape
    if T == target_len:
        return arr.astype(np.float32)
    x_old = np.linspace(0, 1, T, endpoint=True)
    x_new = np.linspace(0, 1, target_len, endpoint=True)
    out = np.zeros((target_len, C), dtype=np.float32)
    for c in range(C):
        out[:, c] = np.interp(x_new, x_old, arr[:, c])
    return out

def _load_csv_first_n_cols(path: str, n_cols: int, target_len: int, normalize: bool) -> np.ndarray:
    """
    Hỗ trợ file có header và cột timestamp.
    Ưu tiên lấy theo thứ tự tên cột:
      ['accX1','accY1','accZ1','gyrX1','gyrY1','gyrZ1',
       'accX2','accY2','accZ2','gyrX2','gyrY2','gyrZ2']
    Nếu không đủ thì rơi về 12 cột số đầu tiên.
    """
    desired_cols = [
        "accX1","accY1","accZ1","gyrX1","gyrY1","gyrZ1",
        "accX2","accY2","accZ2","gyrX2","gyrY2","gyrZ2",
    ]

    # Đọc CSV, cho phép header, bỏ dòng lỗi
    df = pd.read_csv(path, engine="python", on_bad_lines="skip")

    # Bỏ các cột timestamp nếu có
    for col in list(df.columns):
        if str(col).lower() in ("timestamp", "time", "datetime"):
            df = df.drop(columns=[col])
        elif df[col].dtype == object and df[col].astype(str).str.contains("T").any():
            # cột có dạng ISO time -> bỏ
            try:
                pd.to_datetime(df[col], errors="raise")
                df = df.drop(columns=[col])
            except Exception:
                pass

    # Nếu đủ tên cột mong muốn thì sắp xếp đúng thứ tự
    if set(desired_cols).issubset(set(df.columns)):
        df = df[desired_cols]
    else:
        # Ép numeric toàn bộ, giữ lại các cột số
        for c in df.columns:
            df[c] = pd.to_numeric(df[c], errors="coerce")
        df = df.select_dtypes(include=[np.number])

        # Nếu > n_cols thì lấy n_cols đầu; nếu < n_cols thì pad 0
        if df.shape[1] >= n_cols:
            df = df.iloc[:, :n_cols]
        else:
            # pad cột 0
            need = n_cols - df.shape[1]
            for i in range(need):
                df[f"_pad{i}"] = 0.0
            df = df.iloc[:, :n_cols]

    # Xử lý NaN: nội suy theo cột rồi fill 0
    df = df.apply(lambda s: s.interpolate(limit_direction="both"))
    df = df.fillna(0.0)

    arr = df.to_numpy(dtype=np.float32)  # [T, n_cols]

    if normalize:
        mean = arr.mean(axis=0, keepdims=True)
        std = arr.std(axis=0, keepdims=True) + 1e-6
        arr = (arr - mean) / std

    arr = _resample_to_len(arr, target_len)  # [700, n_cols]
    return arr

# Dataset cho 1 sensor (giữ lại nếu cần dùng riêng)
class SingleSensorTimeSeries(Dataset):
    """
    Trả về tensor [12, 700] cho một sensor (ví dụ 'golfer_belt').
    """
    def __init__(self, artifacts_dir: str, split_file: str, device_name: str, n_cols: int = 12):
        art = Path(artifacts_dir)
        self.sessions: Dict[str, Dict[str, str]] = json.loads((art / "sessions.json").read_text())
        self.keys_all: List[str] = json.loads((art / split_file).read_text())
        cfg: Dict = json.loads((art / "config.json").read_text())

        self.device_name = device_name
        self.seq_len = int(cfg.get("seq_len", 700))
        self.normalize = bool(cfg.get("normalize", True))
        self.n_cols = int(n_cols)

        self.keys: List[str] = [k for k in self.keys_all if self.device_name in self.sessions.get(k, {})]
        self.total_channels = self.n_cols

    def __len__(self) -> int:
        return len(self.keys)

    def __getitem__(self, idx: int):
        key = self.keys[idx]
        csv_path = self.sessions[key][self.device_name]
        arr = _load_csv_first_n_cols(csv_path, self.n_cols, self.seq_len, self.normalize)
        x = torch.from_numpy(arr.T.copy())  # [12, 700]
        return x, key

# Dataset ghép 3 sensor -> 36 kênh
class MultiSensorTimeSeries(Dataset):
    """
    Đầu vào gồm 3 sensor theo thứ tự: golfer_belt, golfer_coxa, golfer_glove.
    - Mỗi sensor dùng 12 cột, resample về 700 bước.
    - Thiếu sensor -> padding zero [700, 12].
    Trả về tensor [36, 700].
    """
    def __init__(self, artifacts_dir: str, split_file: str,
                 device_order: List[str] = ("golfer_belt", "golfer_coxa", "golfer_glove"),
                 n_cols_per_sensor: int = 12):
        art = Path(artifacts_dir)
        self.sessions: Dict[str, Dict[str, str]] = json.loads((art / "sessions.json").read_text())
        self.keys: List[str] = json.loads((art / split_file).read_text())
        cfg: Dict = json.loads((art / "config.json").read_text())

        self.device_order = list(device_order)
        self.seq_len = int(cfg.get("seq_len", 700))
        self.normalize = bool(cfg.get("normalize", True))
        self.n_cols = int(n_cols_per_sensor)

        self.total_channels = self.n_cols * len(self.device_order)

    def __len__(self) -> int:
        return len(self.keys)

    def _session_to_tensor(self, key: str) -> torch.Tensor:
        chunks = []
        for dev in self.device_order:
            path = self.sessions.get(key, {}).get(dev, None)
            if path:
                arr = _load_csv_first_n_cols(path, self.n_cols, self.seq_len, self.normalize)  # [700, 12]
            else:
                arr = np.zeros((self.seq_len, self.n_cols), dtype=np.float32)
            chunks.append(arr)
        stacked = np.concatenate(chunks, axis=1)  # [700, 36]
        return torch.from_numpy(stacked.T.copy())  # [36, 700]

    def __getitem__(self, idx: int):
        key = self.keys[idx]
        x = self._session_to_tensor(key)
        return x, key

def collate_batch(batch):
    xs, keys = zip(*batch)
    x = torch.stack(xs, dim=0)  # [B, C, L]
    return x, list(keys)
