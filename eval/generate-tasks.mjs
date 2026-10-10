// 由 cases/cases.json 生成 pigeon eval 的题目目录：每道题一个 <输出目录>/<id>/，含 task.json、task.md、README.md、verify.mjs。
//
// 题目目录在每台主机上现场生成、不入库：改动用例与命令行程序的位置因主机而异，生成时写成绝对路径。
// 题面（task.md）只交代按什么顺序调哪些工具，不含预期结论；预期与改动触及的组件只写进 verify.mjs 的 judge 参数。
// 工作树取仓库的第一个提交（只有 .gitignore）：Agent 在工作树里看不到任何用例与实测结论，题目也用不到工作树里的文件。
//
// 用法：node eval/generate-tasks.mjs --cli <patchparity.exe> --out <题目目录> [--cases <cases 目录>]
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "..");

function parseArgs(argv) {
  const args = { cases: path.join(repoRoot, "cases") };
  for (let i = 0; i < argv.length; i += 2) {
    const [flag, value] = [argv[i], argv[i + 1]];
    if (value === undefined) throw new Error(`参数 ${flag} 缺少值`);
    if (flag === "--cli") args.cli = path.resolve(value);
    else if (flag === "--out") args.out = path.resolve(value);
    else if (flag === "--cases") args.cases = path.resolve(value);
    else throw new Error(`未知参数 ${flag}`);
  }
  if (!args.cli || !args.out) {
    throw new Error("用法：node eval/generate-tasks.mjs --cli <patchparity.exe> --out <题目目录> [--cases <cases 目录>]");
  }
  return args;
}

// Pigeon 的任务 id 规则：与目录名一致，最长 24 个字符（src/eval/task.ts:31）
const ID_PATTERN = /^[a-z0-9][a-z0-9-]{0,23}$/;
const CATEGORIES = { safe: "安全", dangerous: "危险", boundary: "边界" };
const MEASURED = new Set(["equivalent", "divergent", "patch_failed"]);

// 预算按 Pigeon 会话的建议（答复 Q6）：单题约 25 到 40 次工具调用，轮次留两倍余量；
// 墙钟宁长勿短——撞上限时服务端多半正在 wait_job 里，会在 2 秒后被强杀（裁决 015）
const BUDGET = { maxTurns: 80, wallClockMs: 60 * 60 * 1000 };

function rootCommit() {
  return execFileSync("git", ["-C", repoRoot, "rev-list", "--max-parents=0", "HEAD"], { encoding: "utf8" })
    .trim()
    .split("\n")[0];
}

function taskMarkdown(c, patchPath) {
  return `# 验证一个热更补丁

用 patchparity 的 MCP 工具，按下面的顺序完成一次补丁等价性验证。判定由工具链根据工具调用留下的记录自动给出，你不需要、也不应该自己下结论。

## 本题参数

- patch_path：\`${patchPath}\`
- patch_name：\`${c.id}\`
- scenario：\`${c.scenario}\`

## 规则

- 只使用 patchparity 的工具（名字以 \`mcp__patchparity__\` 开头）。不要使用 edit_file、run_command 等内置工具，不要读写任何文件。
- build_package、build_patch、run_scenario 会立即返回一个 job_id。随后反复调用 wait_job（job_id 取该值，max_wait_seconds 取 45），直到返回的 status 不再是 running。上一步完成后再开始下一步。
- 某一步的结果为 failed 时不要重试，直接跳到最后一步撤销改动。
- 无论前面成功与否，最后一步撤销改动都必须执行。

## 步骤

1. build_package，不带参数。完成后记下返回的 package，这是母包基线。
2. build_patch，patch_path 与 patch_name 取本题参数。
3. run_scenario：package 取第 1 步的母包基线，patch_name 取本题参数，scenario 取本题参数，run_name 为 \`patched-1\`。
4. 与第 3 步相同，run_name 为 \`patched-2\`（确定性自检）。
5. build_package，不带参数。此时改动处于施加状态，得到的是整包重编的包，package 与第 1 步不同。
6. run_scenario：package 取第 5 步的包，不带 patch_name，scenario 取本题参数，run_name 为 \`rebuild\`。
7. compare_runs：left 为 \`patched-1\`，right 为 \`patched-2\`。
8. compare_runs：left 为 \`rebuild\`，right 为 \`patched-1\`。
9. revert_patch，patch_path 取本题参数。

全部完成后，用一两句话说明各步是否成功即可。
`;
}

function readme(c) {
  return `# ${c.id}

- 测什么：patchparity 能否对这个改动给出正确判定。类别：${CATEGORIES[c.category]}。
- 改动：${c.note}
- 来源：patchparity 仓库 \`cases/${c.patch}\`，由 \`eval/generate-tasks.mjs\` 依 \`cases/cases.json\` 生成，勿手改。
- 判分：\`verify.mjs\` 调用 \`patchparity judge\`，从本题工作树里的台账 \`patchparity-ledger.json\` 判定并与预期比对（裁决 009）。
- 许可：随 patchparity 仓库；改动施加在 xpTURN/Klotho（Apache-2.0）的样例源码上。
`;
}

function verifyScript(cli, judgeArgs) {
  return `// 判分：用 patchparity judge 从本题工作树里的台账判定，并与预期比对。由 eval/generate-tasks.mjs 生成，勿手改。
// 退出码即判分：0 符合预期，非 0 不符合。judge 的最后一行输出是 JSON 摘要，pigeon eval 会原样收进结果文件。
import { spawnSync } from "node:child_process";
import path from "node:path";

const cli = ${JSON.stringify(cli)};
const ledger = path.join(process.cwd(), "patchparity-ledger.json");
const result = spawnSync(cli, ["judge", ledger, ...${JSON.stringify(judgeArgs)}], { stdio: "inherit" });
if (result.error) {
  console.error("无法运行 patchparity judge：" + result.error.message);
}
process.exit(typeof result.status === "number" ? result.status : 1);
`;
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  if (!existsSync(args.cli)) throw new Error(`找不到命令行程序 ${args.cli}`);
  const manifest = JSON.parse(readFileSync(path.join(args.cases, "cases.json"), "utf8"));
  const ref = rootCommit();

  // 每次整体重生成：清单里删掉的题不能留在目录里继续被跑
  rmSync(args.out, { recursive: true, force: true });
  const seen = new Set();
  for (const c of manifest.cases) {
    if (!ID_PATTERN.test(c.id)) throw new Error(`题目 id 不合 Pigeon 的规则（小写字母数字与连字符，最长 24）：${c.id}`);
    if (seen.has(c.id)) throw new Error(`题目 id 重复：${c.id}`);
    seen.add(c.id);
    if (!CATEGORIES[c.category]) throw new Error(`未知类别 ${c.category}（${c.id}）`);
    if (c.measured !== undefined && (c.category !== "boundary" || !MEASURED.has(c.measured))) {
      throw new Error(`measured 只用于边界题，且取 equivalent、divergent 或 patch_failed（${c.id}）`);
    }
    const patchPath = path.join(args.cases, c.patch);
    if (!existsSync(patchPath)) throw new Error(`找不到改动用例 ${patchPath}`);

    const judgeArgs = ["--expect", c.category, "--case", c.id];
    if (c.touched.length > 0) judgeArgs.push("--touched", c.touched.join(","));
    if (c.measured) judgeArgs.push("--measured", c.measured);

    const dir = path.join(args.out, c.id);
    mkdirSync(dir, { recursive: true });
    const task = {
      version: 1,
      id: c.id,
      instructions: "task.md",
      repo: { path: ".", ref },
      budget: BUDGET,
      verifier: { command: ["node", "{TASK_DIR}/verify.mjs"], timeoutMs: 120000 },
      assets: [],
      tags: ["patch-verify", c.category],
      holdout: false,
    };
    writeFileSync(path.join(dir, "task.json"), JSON.stringify(task, null, 2) + "\n");
    writeFileSync(path.join(dir, "task.md"), taskMarkdown(c, patchPath));
    writeFileSync(path.join(dir, "README.md"), readme(c));
    writeFileSync(path.join(dir, "verify.mjs"), verifyScript(args.cli, judgeArgs));
  }
  console.log(`已生成 ${manifest.cases.length} 道题到 ${args.out}（工作树取提交 ${ref.slice(0, 7)}）`);
}

main();
