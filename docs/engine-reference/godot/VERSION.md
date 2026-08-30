# Godot — Version Reference

| Field | Value |
|-------|-------|
| **Engine Version** | 4.3 stable (official 77dcf97) |
| **Project Pinned** | 2026-08-31 |
| **LLM Knowledge Cutoff** | 2026-01 |
| **Risk Level** | LOW — 4.3 在 LLM 训练数据内（训练覆盖至 ~4.3+） |

## Note

Godot 4.3 stable 在训练数据覆盖范围内，引擎参考库保持最小化（本文件）。
若后续升级到 4.4/4.5+（超出训练覆盖），运行 `/setup-engine upgrade 4.3 <new>` 生成
breaking-changes / deprecated-apis / current-best-practices 全套参考。

白盒验证记录：prototypes/bmg-whitebox/ 在 4.3 无头模式（--headless，GDScript 60Hz 确定性步进）
下完成 6000 节点穷举审计与 20/20 帧一致性校验，引擎稳定性已实证。

Last verified: 2026-08-31
