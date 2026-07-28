# -*- coding: utf-8 -*-
"""Oyuncu sesleri sentezi (yalniz numpy; scipy gerekmez).

Uretilenler -> Assets/_VRMultiplayer/Resources/WeaponSounds/:
  footstep_1..4.wav  : beton uzerinde bot adimi (topuk + burun cift vurusu, varyantli)
  equip_generic.wav  : silah/ekipman ele alma (kumas hisirti + metalik sak-sak)
  hit_body_1..3.wav  : merminin vucuda girisi ("tik" klik + "puf" govde + islak doku)

Tasarim notu: gercekci darbe sesleri uc katmandan olusur — (1) genis bantli cok kisa
KLIK transient (kulak "temas ani"ni bundan okur), (2) dusuk frekansli govde THUMP'i
(agirlik/buyukluk hissi), (3) malzeme dokusu (kumas/eti/cakil gurultusu). Katman
zamanlamalari ve zarflari milisaniye olceginde ayarli; degistirirken kucuk adimlarla oyna.

Kullanim:  python tools/generate_player_sounds.py
"""
import os
import wave
import numpy as np

SR = 44100
OUT = os.path.join(os.path.dirname(__file__), "..",
                   "Assets", "_VRMultiplayer", "Resources", "WeaponSounds")


# ------------------------------------------------------------------ temel araclar

def timeline(dur):
    return np.arange(int(SR * dur)) / SR


def fft_filter(x, lo=None, hi=None, slope=2.0):
    """Butterworth benzeri yumusak egimli spektral filtre (lo=highpass, hi=lowpass)."""
    n = len(x)
    X = np.fft.rfft(x)
    f = np.fft.rfftfreq(n, 1.0 / SR)
    H = np.ones_like(f)
    if hi is not None:
        H *= 1.0 / np.sqrt(1.0 + (f / hi) ** (2 * slope))
    if lo is not None:
        with np.errstate(divide="ignore", invalid="ignore"):
            r = (f / lo) ** slope
            hp = r / np.sqrt(1.0 + r * r)
        hp[0] = 0.0
        H *= hp
    return np.fft.irfft(X * H, n)


def exp_env(dur, tau, attack=0.0015):
    """Cok kisa atak + ussel sonum: darbe seslerinin dogal zarfi."""
    tt = timeline(dur)
    env = np.exp(-tt / tau)
    a = max(1, int(SR * attack))
    env[:a] *= np.linspace(0.0, 1.0, a)
    return env


def thump(dur, f_start, f_end, tau, rng=None):
    """Frekansi asagi kayan sonumlu sinus — darbenin 'govdesi'. Kayma onemli:
    sabit frekansli sinus 'bip' gibi durur, asagi kayan sinus 'vurus' gibi."""
    tt = timeline(dur)
    k = np.log(f_end / f_start)
    phase = 2 * np.pi * f_start * (np.expm1(k * tt / dur)) * dur / k
    return np.sin(phase) * exp_env(dur, tau)


def noise_burst(dur, tau, lo, hi, rng, attack=0.0015):
    x = rng.standard_normal(int(SR * dur)) * exp_env(dur, tau, attack)
    return fft_filter(x, lo=lo, hi=hi)


def click(dur, hi, rng, amp=1.0):
    """1-2 ms genis bantli transient: 'temas ani' algisi."""
    n = int(SR * dur)
    x = rng.standard_normal(n) * np.linspace(1.0, 0.0, n) ** 2
    return fft_filter(x, hi=hi) * amp


def ring(dur, freq, tau, amp):
    """Metalik cinlama kismi (tek partial)."""
    tt = timeline(dur)
    return np.sin(2 * np.pi * freq * tt) * np.exp(-tt / tau) * amp


def place(canvas, snd, at):
    i = int(SR * at)
    j = min(len(canvas), i + len(snd))
    if j > i:
        canvas[i:j] += snd[: j - i]


def write_wav(name, x, peak=0.85):
    x = np.asarray(x, dtype=np.float64)
    f = max(1, int(SR * 0.003))  # 3 ms kenar solmasi: tik/pop onler
    x[:f] *= np.linspace(0.0, 1.0, f)
    x[-f:] *= np.linspace(1.0, 0.0, f)
    x = x / (np.max(np.abs(x)) + 1e-9) * peak
    # yumusak sinirlayici: katman toplaminda olasi sivri tepeleri kirpmadan bastirir
    x = np.tanh(x * 1.25) / np.tanh(1.25)
    path = os.path.join(OUT, name)
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes((x * 32767).astype("<i2").tobytes())
    print("yazildi:", os.path.normpath(path), f"({len(x)/SR:.2f} sn)")


# ------------------------------------------------------------------ 1) ayak sesleri

def footstep(seed):
    """Beton uzerinde bot: topuk vurusu + ~110 ms sonra daha hafif burun vurusu +
    aralarinda cok kisik surtme. Varyantlar zamanlama/perde titremesiyle ayrisir."""
    rng = np.random.default_rng(seed)
    out = np.zeros(int(SR * 0.30))
    jit = lambda a, b: rng.uniform(a, b)

    # Topuk: govde thump'i + tok cakil/asfalt dokusu
    f0 = 105 * jit(0.92, 1.08)
    place(out, thump(0.09, f0, f0 * 0.62, 0.028) * 1.0, 0.0)
    place(out, noise_burst(0.05, 0.011, 180, 2100 * jit(0.85, 1.15), rng) * 0.55, 0.0)
    place(out, click(0.0016, 3200, rng, 0.28), 0.0)

    # Surtme: burun basmadan hemen once ayakkabi tabani kayar (cok kisik)
    place(out, noise_burst(0.09, 0.05, 700, 4200, rng, attack=0.02) * 0.07, jit(0.04, 0.06))

    # Burun: daha tiz, daha hafif ikinci vurus
    ta = jit(0.10, 0.125)
    f1 = 135 * jit(0.92, 1.08)
    place(out, thump(0.07, f1, f1 * 0.65, 0.020) * 0.50, ta)
    place(out, noise_burst(0.04, 0.009, 260, 2600 * jit(0.85, 1.15), rng) * 0.38, ta)
    return out


# ------------------------------------------------------------------ 2) ele alma sesi

def equip(seed=11):
    """Silahi kavrama: kisa kumas/askı hisirtisi, ardindan iki metalik 'sak' (elin
    kabzaya oturmasi + aksamin yerine yatmasi) ve tok bir el temasi."""
    rng = np.random.default_rng(seed)
    out = np.zeros(int(SR * 0.34))

    # Kumas hisirtisi: yukselen zarfli bant-gecirilmis gurultu
    n = int(SR * 0.13)
    sw = rng.standard_normal(n) * np.sin(np.linspace(0, np.pi, n)) ** 1.5
    place(out, fft_filter(sw, lo=450, hi=3200) * 0.30, 0.0)

    # 1. sak: el kabzaya oturur — klik + metal partial'lar + tok temas
    t1 = 0.115
    place(out, click(0.002, 5800, rng, 0.95), t1)
    place(out, ring(0.07, 1480, 0.020, 0.42), t1)
    place(out, ring(0.05, 2350, 0.011, 0.30), t1)
    place(out, ring(0.03, 3320, 0.007, 0.18), t1)
    place(out, thump(0.06, 150, 95, 0.022) * 0.45, t1)

    # 2. sak: aksam yerine yatar — ayni ailenin daha kisa/hafif kopyasi
    t2 = 0.195
    place(out, click(0.0016, 5200, rng, 0.55), t2)
    place(out, ring(0.05, 1720, 0.014, 0.28), t2)
    place(out, ring(0.03, 2680, 0.008, 0.16), t2)
    place(out, thump(0.05, 130, 88, 0.018) * 0.28, t2)
    return out


# ------------------------------------------------------------------ 3) vucuda mermi

def hit_body(seed):
    """Merminin vucuda girisi — kurbanin KENDI kulaginda caldigi icin yakin-mikrofon
    karakterinde: kisa 'tik' temas kligi + derin 'puf' govde vurusu + islak doku."""
    rng = np.random.default_rng(seed)
    out = np.zeros(int(SR * 0.22))
    jit = lambda a, b: rng.uniform(a, b)

    # 'Tik': temas ani (cok kisa, orta-tiz)
    place(out, click(0.0018, 3600 * jit(0.9, 1.1), rng, 0.55), 0.0)

    # 'Puf': derin govde vurusu — sesin agirligi buradan gelir
    f0 = 150 * jit(0.9, 1.1)
    place(out, thump(0.11, f0, 62, 0.045) * 1.0, 0.001)

    # Islak doku: alcak-gecirilmis kisa gurultu (et yumusakligi)
    place(out, noise_burst(0.09, 0.032, 130, 700 * jit(0.85, 1.2), rng) * 0.60, 0.002)

    # Cok kisik ust doku: giysi/deri kisa hisirti
    place(out, noise_burst(0.04, 0.012, 500, 2400, rng) * 0.15, 0.001)
    return out


# ------------------------------------------------------------------ calistir

if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    for i in range(4):
        write_wav(f"footstep_{i + 1}.wav", footstep(seed=20 + i))
    write_wav("equip_generic.wav", equip())
    for i in range(3):
        write_wav(f"hit_body_{i + 1}.wav", hit_body(seed=40 + i), peak=0.9)
    print("tamam.")
