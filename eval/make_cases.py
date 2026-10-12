"""由文本替换生成改动用例：在被测样例上逐处替换、取 git diff 写成 cases/<名字>.patch，再还原。

用法：python eval/make_cases.py [名字 ...]   不给名字则全部重新生成。
只覆盖本表里的用例；早期手写的用例（safe-item-lifetime、boundary-new-bcl-dependency、danger-aot-references-hotupdate）不在其中。
生成后须在云上实测，类别以实测为准（cases/README.md）。
"""
import pathlib
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
VENDOR = REPO / "vendor" / "Klotho"
CASES = REPO / "cases"
B = "Samples/Brawler/Assets/Brawler/Scripts/ECS/"
R = "com.xpturn.klotho/Runtime/"

cases = {
    "safe-platform-speed": [
        (B + "BrawlerSimSetup.cs",
         "MoveSpeed      = FP64.FromDouble(0.1),",
         "MoveSpeed      = FP64.FromDouble(0.2),"),
    ],
    "safe-gravity": [
        (B + "Systems/TopdownMovementSystem.cs",
         "body.velocity.y = body.velocity.y - asset.GravityAccel * dt;",
         "body.velocity.y = body.velocity.y - asset.GravityAccel * dt * FP64.FromInt(2);"),
    ],
    "safe-stock-count": [
        (B + "Systems/PlatformerCommandSystem.cs",
         "character.StockCount      = 3;",
         "character.StockCount      = 2;"),
    ],
    "boundary-item-new-field": [
        (B + "Components/ItemComponent.cs",
         "        public long EntityId;        // EntityRef.ToId() — used as view registry key\n",
         "        public long EntityId;        // EntityRef.ToId() — used as view registry key\n"
         "        public int SpawnTick;        // Tick the item was spawned on\n"),
        (B + "Systems/ItemSpawnSystem.cs",
         "            item.EntityId = entity.ToId();\n",
         "            item.EntityId = entity.ToId();\n"
         "            item.SpawnTick = frame.Tick;\n"),
    ],
    "boundary-float-cast": [
        (B + "Systems/ItemSpawnSystem.cs",
         "            item.RemainingTicks = config.ItemLifetimeTicks;\n",
         "            // Out-of-range float to int at run time: the result depends on the conversion semantics\n"
         "            int scaled = (int)(frame.Tick * 1.0e9f);\n"
         "            item.RemainingTicks = config.ItemLifetimeTicks + (scaled < 0 ? 1 : 0);\n"),
    ],
    "boundary-field-reorder": [
        (B + "Components/PlatformComponent.cs",
         "        public bool IsMoving;\n        public FPVector3 Waypoint0;\n",
         "        public FPVector3 Waypoint0;\n        public bool IsMoving;\n"),
    ],
    "boundary-filter5": [
        (B + "Systems/PlatformerCommandSystem.cs",
         "var filter = frame.Filter<OwnerComponent, CharacterComponent, PhysicsBodyComponent, TransformComponent>();",
         "var filter = frame.Filter<OwnerComponent, CharacterComponent, PhysicsBodyComponent, TransformComponent, SkillCooldownComponent>();"),
    ],
    "danger-aot-random-edit": [
        (R + "Deterministic/Random/DeterministicRandom.cs",
         "            ulong x = a ^ (b + 0x9E3779B97F4A7C15UL);",
         "            ulong x = a ^ (b + 0x9E3779B97F4A7C17UL);"),
    ],
    "danger-aot-api-signature": [
        (R + "Deterministic/Random/DeterministicRandom.cs",
         "        public static DeterministicRandom FromSeed(ulong worldSeed, ulong featureKey, ulong index = 0)\n"
         "        {\n"
         "            ulong seed = Mix64(worldSeed, featureKey);\n",
         "        public static DeterministicRandom FromSeed(ulong worldSeed, ulong featureKey, ulong index = 0, ulong salt = 0)\n"
         "        {\n"
         "            ulong seed = Mix64(worldSeed, featureKey ^ salt);\n"),
    ],
    "boundary-new-xml-dependency": [
        (B + "Systems/ItemSpawnSystem.cs",
         "            int itemType = rng.NextIntInclusive(0, 2); // 0=Shield, 1=Boost, 2=Bomb\n",
         "            int itemType = rng.NextIntInclusive(0, 2); // 0=Shield, 1=Boost, 2=Bomb\n"
         "            // A new dependency on System.Xml, which nothing in the base package uses\n"
         "            var doc = new System.Xml.XmlDocument();\n"
         "            doc.LoadXml(\"<item scale=\\\"1\\\"/>\");\n"
         "            itemType *= int.Parse(doc.DocumentElement.GetAttribute(\"scale\"));\n"),
    ],
    # ── 场景生成类（裁决 019）：默认场景覆盖不到，须先构造出能让改动可观测的场景 ──
    # 默认场景里移动速度为 0、没有地面，下面这些代码都不执行
    "search-safe-jump-speed": [
        (B + "Systems/PlatformerCommandSystem.cs",
         "                    physics.RigidBody.velocity.y = _movement.JumpSpeed;\n",
         "                    physics.RigidBody.velocity.y = _movement.JumpSpeed * FP64.FromDouble(1.25);\n"),
    ],
    "search-safe-move-formula": [
        (B + "Systems/PlatformerCommandSystem.cs",
         "                physics.RigidBody.velocity.x = cmd.HorizontalAxis * speed;\n",
         "                physics.RigidBody.velocity.x = cmd.HorizontalAxis * speed * FP64.FromDouble(1.5);\n"),
    ],
    "search-safe-hit-reaction": [
        (B + "CombatHelper.cs",
         "            targetChar.HitReactionTicks = hitReactionTicks;\n"
         "\n"
         "            FP64 forceMag = (FP64.FromInt(basePower)\n"
         "                + FP64.FromInt(targetChar.KnockbackPower) * FP64.FromDouble(0.01))\n"
         "                * FP64.FromDouble(0.64);\n",
         "            targetChar.HitReactionTicks = hitReactionTicks;\n"
         "\n"
         "            FP64 forceMag = (FP64.FromInt(basePower)\n"
         "                + FP64.FromInt(targetChar.KnockbackPower) * FP64.FromDouble(0.01))\n"
         "                * FP64.FromDouble(0.8);\n"),
    ],
    # 母包里的击退衰减曲线：只有角色被击中、发生击退时才求值
    "search-danger-curve": [
        (R + "Deterministic/Curve/FPAnimationCurve.cs",
         "            FP64 a = m0 + m1 - dp - dp;\n",
         "            FP64 a = m0 + m1 - dp - dp - dp;\n"),
    ],
    # 母包里的静态射线检测：只有场景里有静态碰撞体（地面）时才会命中
    "search-danger-raycast": [
        (R + "Gameplay/Systems/PhysicsSystem.cs",
         "        public bool RayCastStatic(FPRay3 ray, FP64 maxDistance, out FPVector3 hitPoint, out FPVector3 hitNormal, out FP64 hitDistance)\n"
         "            => _world.RayCastStatic(ray, _bodies, _bodyCount, maxDistance, out hitPoint, out hitNormal, out hitDistance, out _);\n",
         "        public bool RayCastStatic(FPRay3 ray, FP64 maxDistance, out FPVector3 hitPoint, out FPVector3 hitNormal, out FP64 hitDistance)\n"
         "        {\n"
         "            bool hit = _world.RayCastStatic(ray, _bodies, _bodyCount, maxDistance, out hitPoint, out hitNormal, out hitDistance, out _);\n"
         "            hitPoint.y += FP64.FromDouble(0.25);\n"
         "            return hit;\n"
         "        }\n"),
    ],
}


def git(*args):
    return subprocess.run(["git", "-C", str(VENDOR), *args], capture_output=True, text=True, encoding="utf-8", check=True).stdout


def main(names):
    for name, edits in cases.items():
        if names and name not in names:
            continue
        files = sorted({f for f, _, _ in edits})
        originals = {f: (VENDOR / f).read_bytes() for f in files}
        try:
            for f, old, new in edits:
                path = VENDOR / f
                data = path.read_bytes()
                crlf = b"\r\n" in data
                text = data.decode("utf-8-sig" if data.startswith(b"\xef\xbb\xbf") else "utf-8")
                o, n = old, new
                if crlf:
                    o, n = old.replace("\n", "\r\n"), new.replace("\n", "\r\n")
                if text.count(o) != 1:
                    raise SystemExit(f"{name}: expected exactly one match in {f}, found {text.count(o)}")
                text = text.replace(o, n)
                bom = b"\xef\xbb\xbf" if data.startswith(b"\xef\xbb\xbf") else b""
                path.write_bytes(bom + text.encode("utf-8"))
            diff = git("diff", "--", *files)
            # index 行指向上游的 blob 号，对复现无用（见 cases/README.md）
            lines = [l for l in diff.splitlines(keepends=True) if not l.startswith("index ")]
            (CASES / f"{name}.patch").write_text("".join(lines), encoding="utf-8", newline="\n")
            print(f"{name}: {len(lines)} lines, {len(files)} file(s)")
        finally:
            for f, data in originals.items():
                (VENDOR / f).write_bytes(data)


if __name__ == "__main__":
    main(sys.argv[1:])
