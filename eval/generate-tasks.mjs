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
const MEASURED = new Set(["equivalent", "divergent", "patch_failed", "rebuild_failed"]);

// 预算按 Pigeon 会话的建议（答复 Q6）：单题约 25 到 40 次工具调用，轮次留两倍余量；
// 墙钟宁长勿短——撞上限时服务端多半正在 wait_job 里，会在 2 秒后被强杀（裁决 015）
const BUDGET = { maxTurns: 80, wallClockMs: 60 * 60 * 1000 };
// 场景搜索类多出若干轮试场景：每试一次是两次运行加一次比对，约八轮；留足八次尝试的余量
const SEARCH_BUDGET = { maxTurns: 200, wallClockMs: 90 * 60 * 1000 };
const MAX_ATTEMPTS = 8;

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
7. run_scenario：package 取第 1 步的母包基线，不带 patch_name，scenario 取本题参数，run_name 为 \`baseline\`（用来判断改动在此场景下是否可观测）。
8. compare_runs：left 为 \`patched-1\`，right 为 \`patched-2\`。
9. compare_runs：left 为 \`rebuild\`，right 为 \`patched-1\`。
10. revert_patch，patch_path 取本题参数。

全部完成后，用一两句话说明各步是否成功即可。
`;
}

// 场景搜索类题面（裁决 019）：给出改动的 diff 与场景参数的来源，Agent 自己构造让改动可观测的场景。
// 判定仍由 judge 从台账独立算出，并要求改动在选定的场景下可观测；Agent 的说法不作数（裁决 009）。
function searchTaskMarkdown(c, patchPath, diff) {
  return `# 为一个热更补丁构造场景并验证

这个改动在缺省场景下很可能根本不被执行，在那里验证只会得到空洞的"一致"。你的任务是用 patchparity 的 MCP 工具，先构造一个让改动**可观测**的场景，再在该场景下完成补丁等价性验证。判定由工具链根据工具调用留下的记录自动给出，你不需要、也不应该自己下结论。

## 本题参数

- patch_path：\`${patchPath}\`
- patch_name：\`${c.id}\`

## 改动

\`\`\`diff
${diff.trimEnd()}
\`\`\`

${c.context ? `被改的代码在哪里被调用：${c.context}

` : ""}## 规则

- 只使用 patchparity 的工具（名字以 \`mcp__patchparity__\` 开头）。不要使用 edit_file、run_command 等内置工具，不要读写任何文件。
- build_package、build_patch、run_scenario 会立即返回一个 job_id。随后反复调用 wait_job（job_id 取该值，max_wait_seconds 取 45），直到返回的 status 不再是 running。上一步完成后再开始下一步。
- 构建步骤（build_package、build_patch）结果为 failed 时不要重试，直接跳到最后一步撤销改动。
- 无论前面成功与否，最后一步撤销改动都必须执行。
- run_scenario 的场景用 options 给出（k=v;k=v）。先调用 list_scenarios 看可用参数与取值范围。

## 什么叫可观测

在同一个场景下分别运行"母包基线"（未打补丁）与"整包重编"的包，再用 compare_runs 比对二者：判定为 divergent 说明改动改变了模拟状态，即可观测；判定为 equivalent 说明改动在这个场景下没被执行到，需要换场景。缺省场景里角色移动速度为 0、场地没有地面（角色一直下落、反复死亡复活），很多玩法代码因此不执行——结合上面的 diff 想清楚改动在什么情况下才会被执行。

## 步骤

1. list_scenarios，不带参数，读一下自定义场景的参数。
2. build_package，不带参数。完成后记下返回的 package，这是母包基线。
3. build_patch，patch_path 与 patch_name 取本题参数。
4. build_package，不带参数。此时改动处于施加状态，得到的是整包重编的包，package 与第 2 步不同。这个包只建一次。
5. 试场景，最多 ${MAX_ATTEMPTS} 次。第 k 次（k 从 1 开始）：
   1. 拟一个场景的 options。
   2. run_scenario：package 取母包基线，不带 patch_name，options 取该场景，run_name 为 \`baseline-k\`。
   3. run_scenario：package 取整包重编的包，不带 patch_name，options 取同一场景，run_name 为 \`rebuild-k\`。
   4. compare_runs：left 为 \`rebuild-k\`，right 为 \`baseline-k\`。判定为 divergent 即找到了可观测的场景，进入第 6 步；否则据此调整场景再试。
   某次运行 failed（例如参数不合法）时，修正参数后再试，这也算一次尝试。
6. 在找到的场景下（options 与第 5 步最后一次完全相同）：
   1. run_scenario：package 取母包基线，patch_name 取本题参数，run_name 为 \`patched-1\`。
   2. 与上一步相同，run_name 为 \`patched-2\`（确定性自检）。
   3. compare_runs：left 为 \`patched-1\`，right 为 \`patched-2\`。
   4. compare_runs：left 为 \`rebuild-k\`（第 5 步最后一次的），right 为 \`patched-1\`。
   试满 ${MAX_ATTEMPTS} 次仍不可观测时，跳过第 6 步。
7. revert_patch，patch_path 取本题参数。

全部完成后，用一两句话说明找到的场景以及各步是否成功即可。
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
    // 场景搜索类要求改动在选定的场景下可观测：这是客观的停止条件，也是判分的一部分
    if (c.search) judgeArgs.push("--require-observable");

    const dir = path.join(args.out, c.id);
    mkdirSync(dir, { recursive: true });
    const task = {
      version: 1,
      id: c.id,
      instructions: "task.md",
      repo: { path: ".", ref },
      budget: c.search ? SEARCH_BUDGET : BUDGET,
      verifier: { command: ["node", "{TASK_DIR}/verify.mjs"], timeoutMs: 120000 },
      assets: [],
      tags: c.search ? ["patch-verify", "scenario-search", c.category] : ["patch-verify", c.category],
      holdout: false,
    };
    writeFileSync(path.join(dir, "task.json"), JSON.stringify(task, null, 2) + "\n");
    writeFileSync(path.join(dir, "task.md"),
      c.search ? searchTaskMarkdown(c, patchPath, readFileSync(patchPath, "utf8")) : taskMarkdown(c, patchPath));
    writeFileSync(path.join(dir, "README.md"), readme(c));
    writeFileSync(path.join(dir, "verify.mjs"), verifyScript(args.cli, judgeArgs));
  }
  console.log(`已生成 ${manifest.cases.length} 道题到 ${args.out}（工作树取提交 ${ref.slice(0, 7)}）`);
}

main();
