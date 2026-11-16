(() => {
  const data = window.NIAN_PROGRESS;
  if (!data || !Array.isArray(data.groups)) {
    document.getElementById("tree").textContent = "进度数据无法读取，请检查 progress.js。";
    return;
  }
  const node = (tag, className, text) => {
    const el = document.createElement(tag);
    if (className) el.className = className;
    if (text) el.textContent = text;
    return el;
  };
  const tone = (status) => ({"已完成":"done", "部分实测":"tested", "开发中":"active", "待开发":"pending", "待完善":"pending", "待验收":"verify"}[status] || "pending");
  document.getElementById("overview").textContent = data.overview;
  document.getElementById("current").textContent = data.current;
  document.getElementById("updated").textContent = `更新于 ${data.updated}`;
  const tree = document.getElementById("tree");
  data.groups.forEach((group, index) => {
    const section = node("section", "branch");
    const heading = node("div", "branch-heading");
    heading.append(node("span", "branch-number", String(index + 1).padStart(2, "0")));
    const text = node("div");
    text.append(node("h2", "", group.title), node("p", "", group.subtitle));
    heading.append(text); section.append(heading);
    const grid = node("div", "grid");
    group.nodes.forEach((item) => {
      const card = node("details", `card ${tone(item.status)}`);
      const summary = node("summary", "card-summary");
      const top = node("div", "card-top");
      top.append(node("span", `badge ${tone(item.status)}`, item.status), node("span", "expand", "+"));
      summary.append(top, node("h3", "", item.title), node("p", "card-description", item.summary));
      card.append(summary);
      const detail = node("div", "card-detail");
      detail.append(node("p", "", item.detail), node("p", "next", `下一步：${item.next}`));
      if (item.link) {
        const a = node("a", "doc-link", "查看详细计划 →"); a.href = item.link; detail.append(a);
      }
      card.append(detail); grid.append(card);
    });
    section.append(grid); tree.append(section);
  });
})();
