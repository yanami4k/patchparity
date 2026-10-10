// patchparity 接入用的 StreamFn：DeepSeek 官方端点 + pi-ai openai-completions streamSimple。
// 放在冻结副本的 local/ 下只为让模块解析到该副本的 pi-ai；它不属于 Pigeon 的代码，不改动任何既有文件。
// 形状同 spikes/real-stream-fn.mjs：(model, context, options?) => AssistantMessageEventStream。
// CLI 传入的 model 是快照身份占位，忽略之，改用 pi-ai 目录中的真实模型元数据
// （含 requiresReasoningContentOnAssistantMessages 与 thinkingFormat 等兼容开关）。
// 密钥只从环境变量 DEEPSEEK_API_KEY 读，不落任何文件。
import { streamSimple } from "@earendil-works/pi-ai/api/openai-completions";
import { DEEPSEEK_MODELS } from "@earendil-works/pi-ai/providers/deepseek.models";

const apiKey = process.env.DEEPSEEK_API_KEY;
if (!apiKey) {
	throw new Error("缺少 DEEPSEEK_API_KEY 环境变量");
}

// patchparity 的 worker 只按顺序调工具，判定来自流水线而非模型，用快档即可
const modelId = process.env.PATCHPARITY_DEEPSEEK_MODEL ?? "deepseek-v4-flash";
const realModel = DEEPSEEK_MODELS[modelId];
if (!realModel) {
	throw new Error(`pi-ai 目录中找不到 DeepSeek 模型 ${modelId}`);
}

export default function deepseekStreamFn(_model, context, options) {
	return streamSimple(realModel, context, { ...options, apiKey });
}
