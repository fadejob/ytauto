# YtAuto: so'z darajasidagi vaqtlar (faster-whisper, offline). VoiceStudio Python muhitida ishga tushiriladi.
# Foydalanish:
#   python -I align_words.py --check <model>
#   python -I align_words.py <wav> <out_json> <language|auto> <model>
# Chiqish: JSON {language, duration, words:[{word,start,end,prob}], segments:[...], versions}
import sys, os, json, time

os.environ.setdefault("HF_HUB_OFFLINE", "1")


def versions():
    out = {"python": sys.version.split()[0]}
    try:
        import faster_whisper
        out["faster_whisper"] = getattr(faster_whisper, "__version__", "unknown")
    except Exception as e:  # noqa
        out["faster_whisper_error"] = repr(e)
    try:
        import ctranslate2
        out["ctranslate2"] = getattr(ctranslate2, "__version__", "unknown")
    except Exception as e:  # noqa
        out["ctranslate2_error"] = repr(e)
    return out


def check(model):
    v = versions()
    ok = "faster_whisper" in v
    cached = None
    try:
        from huggingface_hub import snapshot_download
        p = snapshot_download(model, local_files_only=True)
        cached = os.path.isfile(os.path.join(p, "model.bin"))
    except Exception as e:  # noqa
        v["model_error"] = repr(e)
        cached = False
    v["model"] = model
    v["model_cached"] = bool(cached)
    v["ok"] = bool(ok and cached)
    print(json.dumps(v))
    return 0 if v["ok"] else 3


def main():
    if len(sys.argv) >= 3 and sys.argv[1] == "--check":
        return check(sys.argv[2])
    wav, out_json, lang, model_name = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
    lang = None if lang in ("", "auto") else lang
    t0 = time.time()
    from faster_whisper import WhisperModel
    m = WhisperModel(model_name, device="cpu", compute_type="int8", cpu_threads=max(1, (os.cpu_count() or 2) // 2))
    t_load = time.time() - t0
    segs, info = m.transcribe(wav, language=lang, word_timestamps=True, vad_filter=False, beam_size=5,
                              condition_on_previous_text=False)
    words, segments = [], []
    for s in segs:
        ws = [{"word": w.word.strip(), "start": round(w.start, 3), "end": round(w.end, 3),
               "prob": round(getattr(w, "probability", 0.0), 3)} for w in (s.words or []) if w.word.strip()]
        words.extend(ws)
        segments.append({"start": round(s.start, 3), "end": round(s.end, 3), "text": s.text.strip()})
    data = {"language": info.language, "language_probability": round(info.language_probability, 3),
            "duration": info.duration, "words": words, "segments": segments, "model": model_name,
            "versions": versions(), "load_sec": round(t_load, 2), "total_sec": round(time.time() - t0, 2)}
    tmp = out_json + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=1)
    os.replace(tmp, out_json)
    print(json.dumps({"words": len(words), "segments": len(segments), "language": info.language,
                      "total_sec": data["total_sec"]}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
