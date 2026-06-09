# Git 工作流

## 分支策略

### main 分支
- 始终保持可发布状态
- 只合并已验证的功能
- 每次提交前确保构建成功

### 功能分支
实验性改动、新功能、bug修复都在独立分支完成：

```bash
# 创建分支
git checkout -b feature/功能名
git checkout -b fix/bug名
git checkout -b experiment/实验名

# 开发完成后合并
git checkout main
git merge feature/功能名
git branch -d feature/功能名

# 或者放弃整个分支
git branch -D experiment/失败的实验
```

---

## 常见场景

### 1. 尝试新功能（不确定是否有效）

```bash
# 在分支上试验
git checkout -b experiment/新字体方案
# ...多次尝试、提交...
git commit -m "尝试方案A"
git commit -m "尝试方案B"

# 失败了 → 直接删分支
git checkout main
git branch -D experiment/新字体方案  # 一切如初

# 成功了 → 合并
git checkout main
git merge experiment/新字体方案
```

### 2. 回退到之前的版本

```bash
# 查看历史
git log --oneline -20

# 创建备份（万一反悔）
git branch backup-$(date +%Y%m%d)

# 硬回退到指定提交
git reset --hard <commit-id>

# 强制推送（如果已推送到远程）
git push -f origin main
```

### 3. 只撤销某个文件

```bash
# 恢复单个文件到上次提交
git checkout HEAD -- 文件路径

# 或从特定提交恢复
git checkout <commit-id> -- 文件路径
```

### 4. 撤销最近的提交但保留改动

```bash
# 撤销最近1个提交，文件改动保留
git reset --soft HEAD~1

# 撤销最近3个提交
git reset --soft HEAD~3

# 重新整理后提交
git add .
git commit -m "整理后的提交信息"
```

### 5. 查看两个版本的差异

```bash
# 查看当前 vs 某个提交
git diff <commit-id>

# 查看两个提交之间的差异
git diff <commit-id1>..<commit-id2>

# 只看某个文件的差异
git diff <commit-id> -- 文件路径
```

---

## 提交信息规范

```
类型: 简短描述（不超过50字符）

详细说明（可选）

类型：
- feat: 新功能
- fix: bug修复
- refactor: 重构
- ui: UI调整
- docs: 文档
- build: 构建配置
- chore: 杂项
```

示例：
```
feat: 添加剪贴板搜索功能

- 支持关键词过滤
- 支持正则表达式
- 快捷键 Ctrl+F
```

---

## 发布流程

### 准备发布

```bash
# 1. 确保在 main 分支
git checkout main

# 2. 打标签
git tag -a v1.0.0 -m "Release v1.0.0"

# 3. 构建便携版
.\build-portable.ps1 -Version "1.0.0"

# 4. 推送标签
git push origin v1.0.0
```

### GitHub Release

1. 访问 https://github.com/kabuda2077/STool/releases
2. 点击 "Draft a new release"
3. 选择标签 v1.0.0
4. 上传 `releases/STool_v1.0.0_Portable.zip`
5. 填写更新日志
6. 发布

---

## 紧急回退（已推送到 GitHub）

```bash
# 1. 备份当前状态
git branch emergency-backup

# 2. 回退到上一个好版本
git reset --hard <commit-id>

# 3. 强制推送（危险操作！）
git push -f origin main

# 注意：如果其他人已经拉取了新代码，会造成混乱
# 单人项目可以放心使用
```

---

## 最佳实践

1. ✅ **每次实验性改动都开新分支**
2. ✅ **提交前先构建验证**
3. ✅ **提交信息清晰描述改动内容**
4. ✅ **定期推送到 GitHub（备份）**
5. ✅ **重要版本打标签（v1.0.0）**
6. ❌ **不要在 main 上进行实验性改动**
7. ❌ **不要提交编译产物（bin/obj）**
8. ❌ **不要强制推送（除非确认安全）**
