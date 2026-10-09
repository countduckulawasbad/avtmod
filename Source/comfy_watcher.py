"""
comfy_watcher.py
================
Called by the RimWorld Avatar mod when "Generate portrait" is clicked:

    python comfy_watcher.py "path/to/pawn.png" "base prompt + detail prompts from game" "{json overrides}"

The 4th argument is an optional JSON blob with workflow overrides. The mod's
AvatarMod.BuildWatcherConfigJson sends `{"checkpoint": "...", "lora_name":
"...", "denoise": 0.65}` so users can swap models / denoise strength from
ModSettings without editing this file. Keys that aren't sent fall back to the
hardcoded CONFIG values.

The single quoted prompts argument from the mod contains:
  - The base preamble (front portrait, {age}-year-old {gender} {race},
    {bodytype}, {lifestage}, ...) with tokens already substituted in-game.
  - Followed by detail prompts (hair, clothes, traits, etc.).

This script:
  1. Parses the incoming prompt to detect the pawn's race.
  2. Prepends a race-specific descriptor block.
  3. Prepends the full mod prompt (so it leads the conditioning).
  4. Appends the static style/quality block that finishes the positive prompt.
  5. Sends the result to a local ComfyUI instance for SDXL img2img.
  6. Background removal is done INSIDE ComfyUI via the ComfyUI-Inspyrenet-
     Rembg node (https://github.com/john-mnz/ComfyUI-Inspyrenet-Rembg), a
     dedicated wrapper around the `transparent_background` PyPI package.
     InSPyReNet preserves clothing, hair strands, and dark/neutral fabric
     far better than rembg/U2Net — those are salient-object segmenters
     that aggressively shave clothes reading as "background-like".

     We previously tried the multi-model ComfyUI-RMBG node by 1038lab,
     which routinely failed auto-install because its requirements.txt
     pulls in ~30 packages for every model it supports (opencv, matplotlib,
     groundingdino, sam2, etc.) — frequently timed out or hit package
     conflicts. The john-mnz wrapper installs in seconds because it has
     exactly one dep (transparent_background).

     The InSPyReNet weights (~360 MB) are auto-downloaded by
     transparent_background on the first generation into the user's
     home cache directory (~/.transparent-background/).

Requirements (one-time install into ComfyUI portable's embedded Python):
    <ComfyUI_windows_portable>\\python_embeded\\python.exe -m pip install pillow requests

This script runs under ComfyUI portable's bundled python_embeded\\python.exe, so
NumPy version pinning is handled by whatever ComfyUI itself depends on — we no
longer auto-downgrade here.

NOTE: You must install the ComfyUI-Inspyrenet-Rembg custom node in your ComfyUI
setup (auto-installed by the mod on first portrait generation):
    https://github.com/john-mnz/ComfyUI-Inspyrenet-Rembg
The node class name is configurable below (default "InspyrenetRembgAdvanced").
"""

import json
import logging
import re
import shutil
import sys
import time
import urllib.request
import urllib.parse
import uuid
from io import BytesIO
from pathlib import Path

# ===========================================================================
# FAILSAFE 1: Python version check
# ===========================================================================
if sys.version_info < (3, 8):
    print(
        f"[ERROR] Python {sys.version_info.major}.{sys.version_info.minor} is too old.\n"
        "This mod requires Python 3.8 or newer.\n"
        "Please install a newer Python version from https://python.org",
        file=sys.stderr,
    )
    sys.exit(1)

# ===========================================================================
# Import dependencies with helpful error messages
# ===========================================================================
try:
    from PIL import Image
    import requests
except ImportError as e:
    missing = str(e).split("'")[1] if "'" in str(e) else str(e)
    print(
        f"\n[ERROR] Missing dependency: {missing}\n"
        "Avatar - Personas needs pillow and requests installed into "
        "ComfyUI portable's embedded Python. Open a terminal and run:\n"
        f"    \"{sys.executable}\" -m pip install pillow requests\n",
        file=sys.stderr,
    )
    sys.exit(1)

# ===========================================================================
# CONFIG
# ===========================================================================
CONFIG = {
    "comfy_host": "127.0.0.1",
    "comfy_port": 8188,
    "checkpoint": "sd_xl_base_1.0.safetensors",
    # Optional external VAE filename (in ComfyUI/models/vae). Empty = use the
    # checkpoint's baked VAE. Some checkpoints (e.g. Pony Diffusion V6 XL) ship a
    # washed-out baked VAE and need an external one; the mod sends it per style.
    "vae_name": "",
    "denoise": 0.65,
    "steps": 30,
    "cfg": 7.5,
    "sampler": "euler_ancestral",
    "scheduler": "karras",
    "width": 1024,
    "height": 1024,
    # Optional pre-VAE upscale of the img2img INPUT. The mod uploads the pawn
    # image at 480x576; SDXL/RealVisXL produce far sharper faces near their
    # native ~1024px, so the Ultra-realistic style scales the input up before
    # VAE-encoding. The stylized style relies on the RimWorld LoRA's structural
    # prior and stays native (upscale_input off). Toggled via the JSON blob.
    "upscale_input": False,
    "upscale_width": 856,
    "upscale_height": 1024,
    "upscale_method": "lanczos",
    # Static style / quality block. The pawn-specific information from the
    # mod is now prepended, so this acts as a *trailing* style modifier.
    "positive_prompt": (
        "natural face, "
        "mugshot, "
    ),
    # When True the style positive_prompt LEADS the prompt instead of trailing
    # the per-pawn description. Pony Diffusion responds best with its score_*
    # tags at the very front. Set per style via the JSON blob.
    "prepend_positive": False,
    # Default negative prompt — user-overridable via ModSettings.
    # The headwear terms (skullcap, kippah, yarmulke, beanie, hat) specifically
    # kill the artifact where SDXL sees a small white spot on top of a bald
    # pixel-art head and hallucinates a yarmulke. The skin terms (patchy, spots,
    # blemishes) cut the speckled-chest texture noise that shows up under open
    # collars. The quality + anatomy terms are standard SDXL negative-prompt
    # boilerplate that improves average output without harming character.
    "negative_prompt": (
        "text, watermark, signature, logo, "
        "tech on face, soft edges, "
        "skullcap, kippah, yarmulke, beanie, hat, cap, headwear, headband, "
        "patchy skin, blemishes, skin spots, acne, freckles, "
        "ugly, deformed, mutated, distorted, disfigured, "
        "extra limbs, extra heads, multiple faces, extra eyes, "
        "asymmetric face, bad anatomy, bad proportions, "
        "low quality, blurry, jpeg artifacts, pixelated"
    ),
    "lora_name": "RimWorld_1.1-000001.safetensors",
    "lora_strength_model": 0.9,
    "lora_strength_clip": 0.9,
    "poll_interval": 2,
    "generation_timeout": 900,
    # ComfyUI-Inspyrenet-Rembg node class name. The Advanced variant exposes
    # a `threshold` input that the basic `InspyrenetRembg` node hardcodes —
    # the basic node's default is too aggressive and chews holes in clothing
    # whose RGB happens to read close to background tones. The advanced node
    # lets us crank threshold low and keep the model's natural soft alpha
    # everywhere it isn't CERTAIN something is background.
    "rmbg_node": "InspyrenetRembgAdvanced",
    # torchscript_jit mode for the Inspyrenet node. "default" uses the
    # node's auto-selection logic (JIT on for first call, off for retries
    # to avoid memory churn). Other valid values: "on", "off".
    "rmbg_torchscript_jit": "default",
    # Mask binarization threshold for the Advanced node.
    #   0.0  = no binarization, keep the model's full soft alpha (preserves
    #          the most foreground detail, may leave faint halos)
    #   0.1  = very conservative — the default we ship. Cuts only obvious
    #          background, keeps clothing and hair strands.
    #   0.5  = the basic node's hardcoded value (too aggressive for portraits)
    #   1.0  = hard binarization, lose all soft edges
    # Overridable from ModSettings via the 4th-CLI-arg JSON blob.
    "rmbg_threshold": 0.1,
    # Post-processing: fill enclosed transparent islands. The InSPyReNet
    # threshold preserves edges but doesn't catch the case where the model
    # paints a hole INSIDE an opaque foreground region (e.g. SDXL drew a
    # dark blob on the chest that the BG model classified as background
    # even though it's surrounded by shirt fabric). Flood-fill from the
    # image edges identifies which transparent pixels are reachable from
    # outside (real background) vs unreachable (enclosed hole = artifact).
    # Enclosed holes are forced to alpha=255, revealing whatever SDXL drew
    # underneath. Default ON — directly fixes the "clothes become transparent"
    # complaint that prompted this code path.
    "hole_fill": True,
    # Post-processing: dilate the alpha mask outward by N pixels via
    # PIL MaxFilter. Counters edge erosion where the model trimmed the
    # body silhouette too aggressively. Trade-off: halos around the
    # figure (background pixels just outside the silhouette get included).
    # 0 = off (default), 1-3 typical, 5 is aggressive.
    "edge_dilate_px": 0,
    # Post-processing: lock the source pixel-art silhouette as an alpha
    # FLOOR. Re-opens the original input image, extracts its alpha (the
    # pixel art is rendered with hideBackground=true so this is a clean
    # silhouette), optionally dilates it, resizes to AI output dims with
    # NEAREST, and ORs it into the AI's alpha channel via ImageChops.lighter.
    #
    # Why: InSPyReNet sometimes mis-classifies parts of the subject as
    # background (dark clothing, hair-against-skin), carving chewy notches
    # or transparent islands INSIDE the figure. The source pixel art has
    # perfect, ground-truth edges — anything that was opaque in the source
    # must stay opaque in the final output. The AI's own alpha is still
    # honored OUTSIDE the source silhouette, so legitimate AI-added detail
    # (longer hair, plumes, shoulder embellishments) is preserved.
    #
    # Composes additively with hole_fill (which handles holes the AI itself
    # creates inside its own silhouette, even where the source had nothing).
    "source_alpha_floor": True,
    # Dilation in AI-output pixel space (1024-scale, not source-scale).
    # 0   = no dilation, exact source silhouette as floor.
    # 4-8 = recommended — adds a small safety band so SDXL-smoothed edges
    #       that fall just outside the pixel-art boundary still get locked in.
    # 12+ = aggressive, may pull in faint halos at the silhouette boundary.
    "source_alpha_dilate_px": 8,
    # Per-channel max RGB threshold for "AI painted background here."
    # The source pixel art is sent to ComfyUI as RGBA but the diffusion
    # pipeline drops alpha and composites onto a BLACK background. SDXL
    # img2img at denoise 0.65 mostly preserves that black, so the area
    # outside the AI's actual portrait framing reads as near-pure-black
    # in RGB even though the source PIXEL ART said "this region is subject"
    # (the source silhouette is a full body, the AI typically only paints
    # a bust). Without this gate, source_alpha_floor would force those
    # pure-black pixels opaque — painting a black silhouette around the
    # actual portrait. With this gate, a pixel can only get floored opaque
    # by the source if the AI actually drew non-background content there
    # (max(R,G,B) > threshold).
    # 15-25 = recommended. Real subject pixels (even dark hair / clothes)
    #         are virtually always brighter than this in at least one channel.
    # 0     = disable the gate (revert to bare OR floor; will paint black
    #         silhouettes around the AI subject).
    "source_alpha_ai_bg_rgb_max": 20,
    # Post-processing: remove the dark matting halo left when the subject was
    # diffused on a BLACK backdrop. For each partially-transparent edge pixel
    # the observed RGB is the true foreground color darkened by black bleeding
    # through (observed ≈ fg * alpha), so dividing RGB by alpha recovers the
    # true bright color — no dark fringe when composited over the light game UI.
    # Off by default; the Ultra-realistic style enables it via the JSON blob.
    "decontaminate_bg": False,
}

# ---------------------------------------------------------------------------
# NOTE: race-descriptor injection used to happen here. It's been REMOVED —
# the C# side (XenotypeDescriptionGenerator) now substitutes the {race} token
# in the base preamble with the FULL face-focused description from
# AIGenPrompts.xml before sending the prompt over. Having a second descriptor
# system here would either duplicate (diluting the prompt) or, worse, fall
# back to a baseliner "standard unmodified human" descriptor when it couldn't
# detect the xenotype name in a prompt that no longer contains xenotype names
# — which is exactly what was making Yttakin pawns render as normal humans.
# Single source of truth = Defs/AIGenPrompts.xml. Edit there to tune.
# ---------------------------------------------------------------------------

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s  %(levelname)-8s  %(message)s",
    datefmt="%H:%M:%S",
)
log = logging.getLogger("comfy_watcher")

# ---------------------------------------------------------------------------
# Prompt assembly
# ---------------------------------------------------------------------------


def build_positive_prompt(mod_prompt: str) -> str:
    """Assemble the final positive prompt:
        [mod base prompt + detail prompts (incl. xenotype description)] +
        [trailing style/quality block]

    The xenotype face descriptor is already baked into mod_prompt by the C#
    side via the {race} token substitution. We just concat and add the style
    block at the end — no race detection here.
    """
    style = CONFIG["positive_prompt"].rstrip(", ")
    mod = mod_prompt.strip().rstrip(",") if (mod_prompt and mod_prompt.strip()) else ""
    # Pony wants its score_* tags FIRST; most other styles append the quality
    # block after the subject description.
    parts = [style, mod] if CONFIG.get("prepend_positive") else [mod, style]
    return ", ".join(p for p in parts if p)


# ---------------------------------------------------------------------------
# ComfyUI helpers
# ---------------------------------------------------------------------------


def comfy_url(path: str) -> str:
    return f"http://{CONFIG['comfy_host']}:{CONFIG['comfy_port']}{path}"


# FAILSAFE 4: Verify ComfyUI is reachable before we start uploading
def _check_comfyui_alive() -> bool:
    try:
        with urllib.request.urlopen(comfy_url("/system_stats"), timeout=10) as resp:
            return resp.status == 200
    except Exception:
        return False


def upload_image(image_path: Path) -> str:
    with open(image_path, "rb") as f:
        files = {"image": (image_path.name, f, "image/png")}
        resp = requests.post(comfy_url("/upload/image"), files=files, timeout=30)
    resp.raise_for_status()
    data = resp.json()
    if "name" not in data:
        raise RuntimeError(f"ComfyUI upload returned unexpected response: {data}")
    return data["name"]


def build_workflow(uploaded_name: str, mod_prompt: str = "") -> dict:
    cfg = CONFIG
    # Treat empty-string lora_name as "disabled" too (the ModSettings UI lets
    # users blank it out to skip the LoRA loader entirely).
    use_lora = bool(cfg.get("lora_name"))
    lora_id = "10"
    model_src = [lora_id, 0] if use_lora else ["1", 0]
    clip_src = [lora_id, 1] if use_lora else ["1", 1]

    positive = build_positive_prompt(mod_prompt)
    log.info("  Positive prompt: %s", positive)

    workflow = {
        "1": {
            "class_type": "CheckpointLoaderSimple",
            "inputs": {"ckpt_name": cfg["checkpoint"]},
        },
        "2": {
            "class_type": "CLIPTextEncode",
            "inputs": {"text": positive, "clip": clip_src},
        },
        "3": {
            "class_type": "CLIPTextEncode",
            "inputs": {"text": cfg["negative_prompt"], "clip": clip_src},
        },
        "4": {"class_type": "LoadImage", "inputs": {"image": uploaded_name}},
        "5": {
            "class_type": "VAEEncode",
            "inputs": {"pixels": ["4", 0], "vae": ["1", 2]},
        },
        "6": {
            "class_type": "KSampler",
            "inputs": {
                "model": model_src,
                "positive": ["2", 0],
                "negative": ["3", 0],
                "latent_image": ["5", 0],
                "seed": int(uuid.uuid4().int % (2**32)),
                "steps": cfg["steps"],
                "cfg": cfg["cfg"],
                "sampler_name": cfg["sampler"],
                "scheduler": cfg["scheduler"],
                "denoise": cfg["denoise"],
            },
        },
        "7": {
            "class_type": "VAEDecode",
            "inputs": {"samples": ["6", 0], "vae": ["1", 2]},
        },
        # Background removal via ComfyUI-Inspyrenet-Rembg's Advanced node so
        # we can tune the mask threshold (clothing-preservation lever). See
        # CONFIG comments above for the threshold scale.
        "8": {
            "class_type": cfg.get("rmbg_node", "InspyrenetRembgAdvanced"),
            "inputs": {
                "image": ["7", 0],
                "threshold": float(cfg.get("rmbg_threshold", 0.1)),
                "torchscript_jit": cfg.get("rmbg_torchscript_jit", "default"),
            },
        },
        "9": {
            "class_type": "SaveImage",
            "inputs": {
                "images": ["8", 0],
                "filename_prefix": "comfy_watcher_out",
            },
        },
    }

    # External VAE override. Some checkpoints' baked VAE is broken/washed-out
    # (Pony V6), so the mod can point the encode + decode at a VAELoader instead
    # of the checkpoint's VAE (["1", 2]). Node id 12 is outside the ids in use.
    vae_name = cfg.get("vae_name")
    if vae_name:
        workflow["12"] = {"class_type": "VAELoader", "inputs": {"vae_name": vae_name}}
        workflow["5"]["inputs"]["vae"] = ["12", 0]
        workflow["7"]["inputs"]["vae"] = ["12", 0]
        log.info("  Using external VAE: %s", vae_name)

    # Pre-VAE input upscale (Ultra-realistic style). Insert an ImageScale node
    # between LoadImage (4) and VAEEncode (5) so the diffusion runs at a higher
    # latent resolution. Node id 11 is outside the 1-10 ids already in use
    # (10 = LoraLoader). The output image inherits these dims; the alpha
    # post-processing resizes the source silhouette to match, so it stays sound.
    if cfg.get("upscale_input"):
        try:
            uw = int(cfg.get("upscale_width", 856))
            uh = int(cfg.get("upscale_height", 1024))
        except (TypeError, ValueError):
            uw, uh = 856, 1024
        workflow["11"] = {
            "class_type": "ImageScale",
            "inputs": {
                "image": ["4", 0],
                "upscale_method": cfg.get("upscale_method", "lanczos"),
                "width": uw,
                "height": uh,
                "crop": "disabled",
            },
        }
        workflow["5"]["inputs"]["pixels"] = ["11", 0]
        log.info("  Upscaling img2img input to %dx%d (%s)", uw, uh, cfg.get("upscale_method", "lanczos"))

    if use_lora:
        workflow[lora_id] = {
            "class_type": "LoraLoader",
            "inputs": {
                "model": ["1", 0],
                "clip": ["1", 1],
                "lora_name": cfg["lora_name"],
                "strength_model": cfg["lora_strength_model"],
                "strength_clip": cfg["lora_strength_clip"],
            },
        }
    return workflow


def queue_prompt(workflow: dict) -> str:
    payload = json.dumps({"prompt": workflow, "client_id": "comfy_watcher"})
    req = urllib.request.Request(
        comfy_url("/prompt"),
        data=payload.encode(),
        headers={"Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            data = json.loads(resp.read())
    except urllib.error.HTTPError as e:
        body = e.read().decode() if e.read else ""
        raise RuntimeError(f"ComfyUI rejected the prompt (HTTP {e.code}): {body}") from e

    if "prompt_id" not in data:
        raise RuntimeError(f"ComfyUI returned unexpected response: {data}")
    return data["prompt_id"]


def wait_for_completion(prompt_id: str):
    deadline = time.time() + CONFIG["generation_timeout"]
    consecutive_failures = 0
    last_error = None
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(
                comfy_url(f"/history/{prompt_id}"), timeout=10
            ) as resp:
                history = json.loads(resp.read())
            consecutive_failures = 0
            if prompt_id in history:
                entry = history[prompt_id]
                # Check for ComfyUI execution errors.
                status = entry.get("status", {})
                if status.get("status_str") == "error":
                    log.error("ComfyUI reported error: %s", status.get("messages"))
                    return None
                # Only return when outputs are populated.
                if entry.get("outputs"):
                    return entry
        except Exception as e:
            # Review #14: don't silently eat poll errors forever. A flaky
            # localhost connection or a ComfyUI crash mid-job would have us
            # politely retrying for the full 300s timeout. Bail after 10
            # consecutive failures so the user gets a real error message.
            consecutive_failures += 1
            last_error = e
            if consecutive_failures >= 10:
                log.error(
                    "Aborting after 10 consecutive /history poll failures. Last error: %s",
                    last_error,
                )
                return None
        time.sleep(CONFIG["poll_interval"])
    return None


def fetch_output_image(history_entry: dict):
    try:
        outputs = history_entry.get("outputs", {})
        for node_id, node_out in outputs.items():
            for img_info in node_out.get("images", []):
                params = urllib.parse.urlencode({
                    "filename": img_info["filename"],
                    "subfolder": img_info.get("subfolder", ""),
                    "type": img_info.get("type", "output"),
                })
                with urllib.request.urlopen(
                    comfy_url(f"/view?{params}"), timeout=30
                ) as resp:
                    return resp.read()
    except Exception as e:
        log.error("Failed to fetch image: %s", e)
    return None


# ---------------------------------------------------------------------------
# Alpha post-processing
# ---------------------------------------------------------------------------


def fill_enclosed_alpha_holes(rgba_img, alpha_threshold=32):
    """Fill enclosed transparent islands in the alpha channel.

    Algorithm:
      1. Build a binary mask where 255 = "possibly background" (alpha below
         threshold) and 0 = "definitely foreground" (alpha above threshold).
      2. Pad the mask by 1px on each side so a single corner flood-fill
         reaches every edge-connected transparent pixel.
      3. Flood-fill from (0, 0) — anything reachable from the new outer
         border = real background, mark it.
      4. Crop back to original size. Any pixel still flagged as transparent
         (255 in our work mask) is NOT reachable from outside = enclosed
         hole = BG-removal artifact. Force its alpha to 255 in the original
         image so the SDXL-rendered RGB underneath becomes visible.

    Why threshold=32: the SDXL-rendered silhouette edges are typically
    anti-aliased over 1-2 pixels (alpha 0..255 gradient). Threshold=32
    means we only flag near-fully-transparent pixels as candidate holes,
    preserving soft edges everywhere. The artifact holes we're fixing are
    typically pure alpha=0, so 32 catches them cleanly.
    """
    from PIL import Image, ImageDraw
    alpha = rgba_img.split()[3]
    w, h = alpha.size

    # Build work mask: 255 = candidate hole, 0 = solid foreground.
    # bytes(...) is C-speed; getdata/putdata loops would be ~50x slower.
    work_data = bytes(255 if v < alpha_threshold else 0 for v in alpha.tobytes())
    work = Image.frombytes('L', (w, h), work_data)

    # Pad by 1px so a corner flood reaches the entire image border.
    padded = Image.new('L', (w + 2, h + 2), 255)
    padded.paste(work, (1, 1))

    # Flood-fill the corner. 255 → 128 for every transparent pixel
    # reachable from the outside.
    ImageDraw.floodfill(padded, (0, 0), 128)

    # Crop back to original size.
    flooded = padded.crop((1, 1, w + 1, h + 1))

    # Pixels still at 255 = enclosed holes. Force alpha to 255 there;
    # leave everything else alone.
    flooded_data = flooded.tobytes()
    alpha_data = alpha.tobytes()
    new_alpha = bytes(
        255 if flooded_data[i] == 255 else alpha_data[i]
        for i in range(len(alpha_data))
    )
    new_alpha_img = Image.frombytes('L', (w, h), new_alpha)
    rgba_img.putalpha(new_alpha_img)
    return rgba_img


def apply_source_alpha_floor(rgba_img, source_path, dilate_px=8, ai_bg_rgb_max=20):
    """Lock the source pixel-art silhouette as a 'must be opaque' alpha floor,
    GATED by whether the AI actually painted content there.

    Re-opens the original input image, extracts its alpha (the pixel-art
    silhouette), resizes + dilates, then computes an "AI has content" mask
    from the AI output's RGB brightness. Only pixels where BOTH (a) the
    source said opaque AND (b) the AI painted non-background content get
    floored opaque. The result is OR'd into the AI's existing alpha.

    Why the AI-content gate: the source pixel art is sent to ComfyUI as
    RGBA but the diffusion pipeline drops alpha and composites onto a black
    background. SDXL img2img at moderate denoise mostly preserves that
    black outside the actual portrait framing. The source silhouette is
    typically a full body (shoulders, torso, hair) while the AI paints
    just a bust — so the source-only floor would paint pure-black opaque
    pixels around the AI's actual subject. The RGB-brightness gate skips
    flooring those pixels: if max(R,G,B) <= ai_bg_rgb_max, that pixel is
    treated as "AI painted nothing here, let it stay transparent."

    Fails open: any error logs a warning and returns the image unchanged.
    """
    from PIL import ImageChops, ImageFilter
    try:
        source = Image.open(source_path)
        source.load()
    except Exception as e:
        log.warning("  Source alpha floor: could not open source image (%s) — skipping", e)
        return rgba_img

    if source.mode != "RGBA":
        log.warning(
            "  Source alpha floor: source image mode is %s, not RGBA — skipping (the mod "
            "is expected to send transparent-background PNGs)", source.mode
        )
        return rgba_img

    source_alpha = source.split()[3]

    # Resize first (NEAREST keeps the silhouette crisp; bilinear would soften
    # the edge into a feathered band that defeats the purpose). Then dilate
    # in AI-output pixel space so the dilation magnitude is consistent
    # regardless of source image size.
    if source_alpha.size != rgba_img.size:
        source_alpha = source_alpha.resize(rgba_img.size, Image.NEAREST)

    if dilate_px > 0:
        kernel_size = 2 * dilate_px + 1
        source_alpha = source_alpha.filter(ImageFilter.MaxFilter(kernel_size))

    # Binarize the source alpha to a hard mask.
    source_alpha = source_alpha.point(lambda v: 255 if v >= 128 else 0)

    # Gate source alpha by "AI actually drew something here." Per-pixel
    # max(R,G,B) > ai_bg_rgb_max means it's not pure background black.
    if ai_bg_rgb_max > 0:
        r, g, b = rgba_img.convert("RGB").split()
        # Per-pixel max across channels via two ImageChops.lighter calls.
        max_channel = ImageChops.lighter(ImageChops.lighter(r, g), b)
        threshold = max(0, min(255, int(ai_bg_rgb_max)))
        ai_has_content = max_channel.point(
            lambda v, t=threshold: 255 if v > t else 0
        )
        # Gate: source_alpha *= ai_has_content (per-pixel, normalized).
        # Multiply does (a * b) / 255, so 255 × 255 = 255, 255 × 0 = 0.
        source_alpha = ImageChops.multiply(source_alpha, ai_has_content)

    ai_alpha = rgba_img.split()[3]
    # Per-pixel max: source's gated opaque pixels override AI's transparency.
    final_alpha = ImageChops.lighter(ai_alpha, source_alpha)
    rgba_img.putalpha(final_alpha)
    return rgba_img


def dilate_alpha_mask(rgba_img, dilate_px):
    """Expand the alpha mask outward by N pixels via PIL MaxFilter.

    Each output pixel = max alpha of all neighbors within N pixels. This
    expands the silhouette outward and fills small chewy notches on the
    edges. Trade-off: pulls in background pixels just outside the original
    silhouette, creating a slight halo. Use sparingly (1-3px) unless edge
    erosion is severe.
    """
    if dilate_px <= 0:
        return rgba_img
    from PIL import ImageFilter
    alpha = rgba_img.split()[3]
    # MaxFilter kernel size must be odd and >= 3. For N=1 → 3, N=2 → 5, etc.
    kernel_size = 2 * dilate_px + 1
    dilated = alpha.filter(ImageFilter.MaxFilter(kernel_size))
    rgba_img.putalpha(dilated)
    return rgba_img


def decontaminate_black_halo(rgba_img):
    """Remove the dark matting fringe from an image matted against a BLACK
    background.

    The subject was diffused on a solid black backdrop, so every
    partially-transparent edge pixel's observed RGB is the true foreground
    color darkened by black bleeding through it (observed ≈ fg * alpha).
    Dividing the RGB by alpha recovers the true (bright) foreground color, so
    when the portrait is later composited over the light game UI there's no
    dark halo around hair/shoulders. Fully-opaque interior pixels and
    fully-transparent background pixels are left untouched.

    The divisor is floored at 0.25 so near-transparent pixels aren't amplified
    into bright speckles (a faint wisp at alpha 0.1 would otherwise get its
    color multiplied 10x). Fails open: any error logs a warning and returns
    the image unchanged.
    """
    try:
        import numpy as np
    except Exception as e:
        log.warning("  Decontaminate: numpy unavailable (%s) — skipping", e)
        return rgba_img
    try:
        arr = np.asarray(rgba_img.convert("RGBA")).astype(np.float32)
        alpha = arr[..., 3]
        a_norm = alpha / 255.0
        safe_a = np.clip(a_norm, 0.25, 1.0)[..., None]
        rgb = arr[..., :3]
        recovered = np.clip(rgb / safe_a, 0.0, 255.0)
        # Only touch partially-transparent edge pixels.
        edge = ((alpha > 0) & (alpha < 255))[..., None]
        arr[..., :3] = np.where(edge, recovered, rgb)
        return Image.fromarray(arr.astype(np.uint8), "RGBA")
    except Exception as e:
        log.warning("  Decontaminate: failed (%s) — skipping", e)
        return rgba_img


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------


def main():
    if len(sys.argv) < 2:
        print("Usage: comfy_watcher.py <image.png> [\"mod prompts\"]")
        sys.exit(1)

    image_path = Path(sys.argv[1]).resolve()
    mod_prompt = sys.argv[2] if len(sys.argv) >= 3 else ""

    # 4th arg: optional JSON config overrides. The mod sends checkpoint /
    # lora_name / denoise from ModSettings here so users can swap them
    # without editing the embedded script. Empty / missing / malformed =
    # use the hardcoded CONFIG defaults (with a warning for malformed).
    if len(sys.argv) >= 4 and sys.argv[3].strip() not in ("", "{}"):
        try:
            overrides = json.loads(sys.argv[3])
            if isinstance(overrides, dict):
                for k, v in overrides.items():
                    if k in CONFIG:
                        CONFIG[k] = v
                        # Don't log the full negative_prompt every gen — it's
                        # long and the user already sees it in mod options.
                        if k == "negative_prompt":
                            log.info("  Config override: negative_prompt = (%d chars)", len(str(v)))
                        else:
                            log.info("  Config override: %s = %r", k, v)
                    else:
                        log.warning("  Unknown config override key: %s (ignored)", k)
        except json.JSONDecodeError as e:
            log.warning("  Could not parse config-override JSON (%s); using defaults", e)

    # FAILSAFE 5: Validate input image
    if not image_path.is_file():
        log.error("Image file not found: %s", image_path)
        sys.exit(1)

    try:
        with open(image_path, "rb") as f:
            header = f.read(8)
        if header[:8] != b"\x89PNG\r\n\x1a\n":
            log.error(
                "Input file does not appear to be a valid PNG image: %s", image_path
            )
            sys.exit(1)
    except Exception as e:
        log.error("Cannot read input image: %s", e)
        sys.exit(1)

    log.info("Processing: %s", image_path.name)
    if mod_prompt:
        log.info("  Mod prompt: %s", mod_prompt)

    # FAILSAFE 4 (continued): Check ComfyUI is reachable
    if not _check_comfyui_alive():
        print(
            "[ERROR] Cannot connect to ComfyUI at "
            f"http://{CONFIG['comfy_host']}:{CONFIG['comfy_port']}\n"
            "Please make sure ComfyUI is running.\n"
            "Expected URL: http://127.0.0.1:8188",
            file=sys.stderr,
        )
        sys.exit(1)

    try:
        uploaded_name = upload_image(image_path)
        workflow = build_workflow(uploaded_name, mod_prompt)
        prompt_id = queue_prompt(workflow)
        log.info("  Queued as: %s", prompt_id)
        history_entry = wait_for_completion(prompt_id)
        if not history_entry:
            log.error("Generation timed out or failed")
            sys.exit(1)
        img_bytes = fetch_output_image(history_entry)
        if not img_bytes:
            log.error("No output image returned")
            sys.exit(1)

        # Post-process pipeline order:
        #   1. hole_fill         — fix enclosed alpha holes (AI/segmenter
        #                          carved a void inside its own silhouette)
        #   2. source_alpha_floor — lock the source pixel-art silhouette as
        #                          a hard opacity floor (segmenter cannot
        #                          carve into anything that was opaque in
        #                          the input pixel art); see CONFIG for
        #                          rationale
        #   3. edge_dilate       — outward expansion of the silhouette to
        #                          counter edge erosion (rare; off by default)
        source_floor_on = CONFIG.get("source_alpha_floor", True)
        hole_fill_on = CONFIG.get("hole_fill", True)
        edge_dilate_px = int(CONFIG.get("edge_dilate_px", 0))
        decontaminate_on = CONFIG.get("decontaminate_bg", False)
        if hole_fill_on or source_floor_on or edge_dilate_px > 0 or decontaminate_on:
            try:
                with BytesIO(img_bytes) as in_buf:
                    img = Image.open(in_buf)
                    img.load()
                if img.mode != "RGBA":
                    img = img.convert("RGBA")
                if hole_fill_on:
                    img = fill_enclosed_alpha_holes(img, alpha_threshold=32)
                    log.info("  Applied hole-fill post-processing")
                if source_floor_on:
                    dilate = int(CONFIG.get("source_alpha_dilate_px", 8))
                    ai_bg_max = int(CONFIG.get("source_alpha_ai_bg_rgb_max", 20))
                    img = apply_source_alpha_floor(
                        img, image_path,
                        dilate_px=dilate,
                        ai_bg_rgb_max=ai_bg_max,
                    )
                    log.info(
                        "  Applied source-alpha floor (dilate=%d px, ai_bg_rgb_max=%d)",
                        dilate, ai_bg_max,
                    )
                if edge_dilate_px > 0:
                    img = dilate_alpha_mask(img, edge_dilate_px)
                    log.info("  Applied edge dilation (%d px)", edge_dilate_px)
                if decontaminate_on:
                    img = decontaminate_black_halo(img)
                    log.info("  Applied black-background decontamination (defringe)")
                with BytesIO() as out_buf:
                    img.save(out_buf, format="PNG")
                    img_bytes = out_buf.getvalue()
            except Exception as e:
                log.warning("  Alpha post-processing failed (%s) — saving raw output", e)

        tmp_path = image_path.with_suffix(".tmp.png")
        with open(tmp_path, "wb") as f:
            f.write(img_bytes)
        shutil.move(str(tmp_path), str(image_path))
        log.info("Done: %s", image_path.name)
    except Exception as e:
        log.error("Failed: %s", e)
        sys.exit(1)


if __name__ == "__main__":
    main()
