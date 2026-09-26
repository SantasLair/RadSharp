"""Converts a GMForms form file (*.gmform.json) to a RADSharp form file (*.radform.json).

Usage: python tools/convert_gmform.py input.gmform.json output.radform.json

What changes (see the brainstorm, Appendix E):
- "format": "gmforms" -> "radform"; integer version -> "1.0"
- snake_case keys -> camelCase (tab_stop -> tabStop, min_size -> minSize, alt_shortcut -> altShortcut)
- classes: MenuBar -> MainMenu (its "menus" become "items"), Toolbar -> ToolBar, Viewport -> PaintBox
- events: Viewport onDraw -> onPaint, onWheel -> onMouseWheel
The output is not canonical; load and save it with RADSharp to normalize it.
"""
import json
import re
import sys

CLASS_MAP = {"MenuBar": "MainMenu", "Toolbar": "ToolBar", "Viewport": "PaintBox"}
EVENT_MAP = {"onDraw": "onPaint", "onWheel": "onMouseWheel"}


def camel(key: str) -> str:
    return re.sub(r"_([a-z])", lambda m: m.group(1).upper(), key)


def convert_item(item: dict) -> dict:
    out = {}
    for k, v in item.items():
        k = camel(k)
        out[k] = [convert_item(i) for i in v] if k == "items" else v
    return out


def convert_node(node: dict) -> dict:
    out = {}
    cls = CLASS_MAP.get(node.get("class", ""), node.get("class"))
    for k, v in node.items():
        if k == "class":
            out["class"] = cls
        elif k == "children":
            out["children"] = [convert_node(c) for c in v]
        elif k == "menus":
            out["items"] = [convert_item(i) for i in v]
        else:
            k = camel(k)
            out[EVENT_MAP.get(k, k) if cls == "PaintBox" else k] = v
    return out


def main(src: str, dst: str) -> None:
    doc = json.load(open(src, encoding="utf-8"))
    out = {"format": "radform", "version": "1.0", "generator": "convert_gmform.py", "root": convert_node(doc["root"])}
    with open(dst, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(out, ensure_ascii=False, indent=2) + "\n")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
