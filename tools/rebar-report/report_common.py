"""Lectura del JSON de rebar_takeoff y de la configuración del reporte, compartida por los generadores."""
import json
import math
import os

SIZE_ORDER_IMPERIAL = ["#11", "#10", "#9", "#8", "#7", "#6", "#5", "#4", "#3", "#2"]
INCH = {"#2": '1/4"', "#3": '3/8"', "#4": '1/2"', "#5": '5/8"', "#6": '3/4"', "#7": '7/8"', "#8": '1"', "#9": '1 1/8"', "#10": '1 1/4"', "#11": '1 3/8"'}


def up(x):
    """Quintales: entero siguiente hacia arriba, desde el valor exacto."""
    return math.ceil(x - 1e-9)


def size_name(key):
    if key.startswith("#"):
        return f"No. {key[1:]} ({INCH.get(key, '')})".replace(" ()", "")
    return f"Ø {key[1:]} mm"


def size_sort(key):
    """Imperiales de mayor a menor, luego milimétricas de mayor a menor."""
    if key.startswith("#"):
        return (0, -int(key[1:]))
    return (1, -float(key[1:]))


def load(config_path):
    cfg = json.load(open(config_path, encoding="utf-8"))
    base = os.path.dirname(os.path.abspath(config_path))

    def resolve(p):
        """Rutas relativas al archivo de configuración."""
        return p if not p or os.path.isabs(p) else os.path.join(base, p)

    tk = resolve(cfg["takeoff"])
    data = json.load(open(tk, encoding="utf-8"))
    out = cfg.get("output", {})
    folder = resolve(out.get("folder")) or os.path.dirname(tk)
    name = out.get("basename") or f"{cfg.get('name', data['document'])} - Acero de refuerzo"
    cfg["_paths"] = {
        "xlsx": os.path.join(folder, name + ".xlsx"),
        "html": os.path.join(folder, name + ".html"),
        "pptx": os.path.join(folder, name + ".pptx"),
    }
    os.makedirs(folder, exist_ok=True)
    cfg.setdefault("levelNames", {})
    cfg.setdefault("criteria", [])
    cfg.setdefault("rfi", [])
    cfg["images"] = {k: resolve(v) for k, v in cfg.get("images", {}).items()}
    return cfg, data


def level_label(cfg, name):
    return cfg["levelNames"].get(name, name)


def bars(data):
    return [r for r in data["rows"] if r["kind"] == "bar"]


def meshes(data):
    return [r for r in data["rows"] if r["kind"] == "mesh"]


def sizes(data):
    return sorted({r["size"] for r in bars(data)}, key=size_sort)
