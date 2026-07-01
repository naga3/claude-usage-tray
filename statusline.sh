#!/usr/bin/env bash
# Claude Code statusline スクリプト
# - stdin で渡される JSON を ~/.claude/usage-monitor/latest.json に保存（トレイアプリが読む）
# - statusline 表示用の1行を stdout に出す
set -u

DIR="$HOME/.claude/usage-monitor"
mkdir -p "$DIR"

input=$(cat)

# rate_limits が入っているときだけ保存する（セッション初回応答前の空データで上書きしない）
if echo "$input" | jq -e '.rate_limits.five_hour.used_percentage' >/dev/null 2>&1; then
  tmp=$(mktemp "$DIR/.latest.XXXXXX")
  printf '%s' "$input" > "$tmp" && mv "$tmp" "$DIR/latest.json"
fi

echo "$input" | jq -r '
  def pct(x): if x == null then "-" else ((x | round | tostring) + "%") end;
  "5h " + pct(.rate_limits.five_hour.used_percentage)
  + " | wk " + pct(.rate_limits.seven_day.used_percentage)
  + (if .context_window.used_percentage != null
     then " | ctx " + pct(.context_window.used_percentage) else "" end)
'
