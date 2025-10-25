
import torch
import torch.nn as nn
import torch.nn.functional as F

class Conv1dAutoEncoder(nn.Module):
    def __init__(self, in_channels: int, seq_len: int, emb_dim: int = 128):
        super().__init__()
        self.enc = nn.Sequential(
            nn.Conv1d(in_channels, 64, 7, stride=2, padding=3),
            nn.BatchNorm1d(64),
            nn.ReLU(True),
            nn.Conv1d(64, 128, 5, stride=2, padding=2),
            nn.BatchNorm1d(128),
            nn.ReLU(True),
            nn.Conv1d(128, 256, 3, stride=2, padding=1),
            nn.BatchNorm1d(256),
            nn.ReLU(True),
        )
        with torch.no_grad():
            h = self.enc(torch.zeros(1, in_channels, seq_len))
            self._enc_L = h.shape[-1]
        self.proj = nn.Linear(256 * self._enc_L, emb_dim)

        self.dec_fc = nn.Linear(emb_dim, 256 * self._enc_L)
        self.dec = nn.Sequential(
            nn.ConvTranspose1d(256, 128, 4, stride=2, padding=1),
            nn.BatchNorm1d(128),
            nn.ReLU(True),
            nn.ConvTranspose1d(128, 64, 4, stride=2, padding=1),
            nn.BatchNorm1d(64),
            nn.ReLU(True),
            nn.ConvTranspose1d(64, in_channels, 4, stride=2, padding=1),
        )

    def encode(self, x):
        h = self.enc(x).flatten(1)
        z = self.proj(h)
        return F.normalize(z, dim=-1)

    def forward(self, x):
        z = self.encode(x)
        h = self.dec_fc(z).view(x.size(0), 256, self._enc_L)
        recon = self.dec(h)
        if recon.size(-1) > x.size(-1):
            recon = recon[..., : x.size(-1)]
        elif recon.size(-1) < x.size(-1):
            recon = nn.functional.pad(recon, (0, x.size(-1) - recon.size(-1)))
        return recon, z
