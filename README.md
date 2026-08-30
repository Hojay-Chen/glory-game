# glory-game —《廿四争锋》(Project ARENA·24)

致敬《全职高手》中「荣耀」战斗机制的 3D 竞技场动作对战游戏。
**只复刻战斗**：24 职业 / 技能取消 / 浮空连招 / 武器系统；不做养成、副本、经济等网游玩法。

## 目录结构

```
glory-game/
├── docs/
│   ├── GDD-Gameplay-v0.1.md   # 玩法总案（内部版本 v0.3.6）
│   ├── 复刻对照审计-v1.md      # 与原著战斗细节的逐条对照审计（含 v2 wiki 核对追记）
│   ├── skill-spec/            # 技能规格书 ✅ v0.1
│   │   ├── README.md          # 字段字典与规范（36 列）
│   │   ├── skills.csv         # 全技能主数据（487 行，唯一事实源）
│   │   ├── 实现注记.md         # 特殊规则实现语义
│   │   └── validation-report.md
│   ├── weapon-spec/           # 武器规格书 ✅ v0.1
│   │   ├── README.md          # 规范 + trait_rules 语法
│   │   ├── weapons.csv        # 武器主数据（73 行 = 24 职业×3 + 万象伞）
│   │   └── validation-report.md
│   ├── balance-sheet/         # 平衡账本 ✅ v0.1（v0.4 补丁后 PASS）
│   │   ├── README.md          # 平衡流程与审计维度
│   │   ├── class-base.csv     # 25 职业面板主数据
│   │   └── balance-report.md  # 审计报告（脚本生成）
│   ├── reference/
│   │   └── 荣耀职业介绍-ycyoc-wiki.md   # 原著考据基准（用户提供的 wiki 抓取存档）
└── tools/
    ├── validate_skills.py     # 技能数据构建+校验
    ├── validate_weapons.py    # 武器数据校验（含 D12 红线）
    ├── balance_audit.py       # 平衡审计（定价带/TTK/连段红线）
    └── learn_levels.py        # 原著习得等级数据
```

## 文档体系规划（后续拆分）

| 文档 | 内容 | 状态 |
|------|------|------|
| GDD-Gameplay | 玩法总案：系统规则 + 24 职业详设（考据版）+ 武器 + 模式框架 | ✅ v0.3 |
| 复刻对照审计 | 与原著战斗细节逐条对照；v2 追记完成 wiki 全量核对 | ✅ v1+v2 |
| Skill-Spec | 技能规格书：487 技能条目全参数 CSV（含习得等级）+ 字段字典 + 实现注记 + 校验 | ✅ v0.1 |
| reference/ | 原著考据材料存档 | ✅ |
| Character-Spec | 职业规格书（每职业一档，完整参数） | 未开始 |
| Weapon-Spec | 武器规格书：73 行主数据 + D12 规则级特性校验 | ✅ v0.1 |
| Balance-Sheet | 平衡账本：职业面板 CSV + 审计器（定价带/TTK/连段红线）；v0.4 补丁后 PASS | ✅ v0.1 |
| Tech-Architecture | 技术架构（引擎、网络、数据管线） | 未开始 |

## IP 合规声明

复刻的是**机制与结构**，不使用原作角色名、战队名、专有剧情名词与独特道具名。
职业名采用通用武侠/奇幻词汇，技能名均为通用汉语词汇组合；发行前建议再做一轮名称商标排查。
