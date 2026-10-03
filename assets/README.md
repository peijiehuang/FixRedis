# 图像来源

- `screenshot.png`：V0.0.1 实际启动窗口，在独立 Windows 桌面启动管理员进程、完成只读检测后，通过 PrintWindow 截取。未触发修复。
- `hero-poster.png`：使用内置 imagegen，以真实截图为界面素材、[PeVersionEditor](https://github.com/peijiehuang/PeVersionEditor) 海报为风格参考生成。原始界面截图另行保留，生成式海报不代替验收证据。

最终修改提示词：

> Precise edit of the existing FixRedis poster. Use the updated real application screenshot as reference. Change the small subtitle inside the application window, immediately below “Redis 服务恢复 · V0.0.1”, to exactly “傻瓜式一键修复，轻轻松松恢复 Redis”, left-aligned in regular muted blue-gray type on one line. Keep the large outside title and the same new tagline unchanged. Update the log timestamp to 22:17:42 to match the new screenshot. Preserve all other UI, buttons, paths, versions, GitHub link, glass background, reflection and framing. No red annotations or redesign.

上一轮外部标题文案调整：

> Edit only the promotional tagline centered immediately below the large external title 'FixRedis V0.0.1' at the top of this poster. Replace the external sentence '先备份，再修复，最后启动并验证服务。' with exactly '傻瓜式一键修复，轻轻松松恢复 Redis'. Render the new sentence in the same crisp dark slate-blue Chinese sans-serif style, one line, centered, with comfortable spacing between the title and app window. Do not change any text inside the application window. Preserve the whole existing application screenshot, title, layout, buttons, data values, log, GitHub link, pale blue glass scenery, reflections, framing and all other pixels as closely as possible. No strike-through line, no annotations or extra copy. Same landscape aspect ratio and image dimensions. This is a precise single-sentence replacement.
