/* ============================================
   GameLauncher — Text Scramble Effect
   文字从乱码逐字解码为正确文字
   ============================================ */

(function () {
    'use strict';

    /**
     * 乱码字符集 — 拉丁字母 / 数字 / 符号 / 假名
     * 中文字符不在乱码集内，以保留视觉对比感
     */
    const CHARS = '!<>-_\\/[]{}—=+*^?#$&%@░▒▓█▌▐|/\\アカサタナハマヤラワ';

    /**
     * TextScramble — 单个元素的解密动画
     * @param {HTMLElement} el 目标元素
     */
    class TextScramble {
        constructor(el) {
            this.el = el;
            this.frame = 0;
            this.frameRequest = null;
            this.resolve = null;
            this.queue = [];
        }

        /**
         * 设置新文本并触发动画
         * @param {string} newText 目标文本
         * @returns {Promise<void>}
         */
        setText(newText) {
            const oldText = this.el.textContent || '';
            const length = Math.max(oldText.length, newText.length);

            this.queue = [];
            for (let i = 0; i < length; i++) {
                const from = oldText[i] || '';
                const to = newText[i] || '';
                const start = Math.floor(Math.random() * 24);
                const end = start + 18 + Math.floor(Math.random() * 24);
                this.queue.push({ from, to, start, end, char: null });
            }

            if (this.frameRequest) cancelAnimationFrame(this.frameRequest);
            this.frame = 0;
            this.update();
            return new Promise((resolve) => { this.resolve = resolve; });
        }

        update() {
            let output = '';
            let complete = 0;

            for (let i = 0, n = this.queue.length; i < n; i++) {
                const item = this.queue[i];
                let { from, to, start, end, char } = item;

                if (this.frame >= end) {
                    complete++;
                    output += to;
                } else if (this.frame >= start) {
                    if (!char || Math.random() < 0.32) {
                        char = CHARS[Math.floor(Math.random() * CHARS.length)];
                        item.char = char;
                    }
                    output += `<span class="dud">${char}</span>`;
                } else {
                    output += from;
                }
            }

            this.el.innerHTML = output;

            if (complete === this.queue.length) {
                if (this.resolve) this.resolve();
            } else {
                this.frameRequest = requestAnimationFrame(() => this.update());
                this.frame++;
            }
        }
    }

    /**
     * 初始化所有 .scramble 元素
     * - 首屏：立即触发解密
     * - 屏外：滚动到视口时触发
     * - 鼠标悬停：重新触发解密
     */
    function init() {
        const targets = Array.from(document.querySelectorAll('.scramble'));
        const map = new WeakMap();

        targets.forEach((el) => {
            const text = el.getAttribute('data-text') || el.textContent;
            el.setAttribute('aria-label', text);
            // 占位层内包含动画层：占位文字撑住布局，动画层精确叠加在其上
            const ghost = document.createElement('span');
            ghost.className = 'scramble__ghost';
            ghost.setAttribute('aria-hidden', 'true');
            ghost.textContent = text;
            const active = document.createElement('span');
            active.className = 'scramble__active';
            active.setAttribute('aria-hidden', 'true');
            active.textContent = text; // 初始即显示最终文字，避免空白闪烁
            ghost.appendChild(active);
            el.innerHTML = '';
            el.appendChild(ghost);
            const scrambler = new TextScramble(active);
            map.set(el, { scrambler, text, played: false });

            // 鼠标悬停时重新触发
            el.addEventListener('mouseenter', () => {
                if (el.dataset.hoverDisabled === 'true') return;
                scrambler.setText(text);
            });
        });

        // 判断元素是否在首屏
        const viewportH = window.innerHeight;

        // 立即处理首屏可见元素
        targets.forEach((el) => {
            const rect = el.getBoundingClientRect();
            if (rect.top < viewportH) {
                const { scrambler, text } = map.get(el);
                // 微小延迟，制造逐元素解码的层次感
                const delay = Math.min(rect.top / viewportH, 1) * 600;
                setTimeout(() => scrambler.setText(text), delay);
                map.get(el).played = true;
            }
        });

        // 监听屏外元素 — 滚动到视口时触发
        if ('IntersectionObserver' in window) {
            const observer = new IntersectionObserver((entries) => {
                entries.forEach((entry) => {
                    if (!entry.isIntersecting) return;
                    const el = entry.target;
                    const data = map.get(el);
                    if (!data || data.played) return;
                    data.played = true;
                    data.scrambler.setText(data.text);
                    observer.unobserve(el);
                });
            }, { threshold: 0.4 });

            targets.forEach((el) => {
                const data = map.get(el);
                if (!data.played) observer.observe(el);
            });
        }

        // 光标跟随效果 — 给页面增加终端感
        const prompt = document.querySelector('.nav__prompt');
        if (prompt) {
            // 已经在 CSS 中以 blink 动画呈现
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();

/* ============================================
   GameLauncher — Typewriter Effect
   正文文字逐字输入显现，结束后光标在末尾闪烁
   ============================================ */

(function () {
    'use strict';

    /**
     * 全局光标闪烁控制器
     * 每 500ms 切换一次，所有注册的光标完全同步，只有亮/灭两态
     * 替代 CSS animation，避免不同时刻创建的光标相位错开
     */
    const blinker = {
        carets: new Set(),
        on: true,
        tick() {
            this.on = !this.on;
            const opacity = this.on ? '1' : '0';
            this.carets.forEach((el) => { el.style.opacity = opacity; });
        },
        register(el) {
            this.carets.add(el);
            el.style.opacity = this.on ? '1' : '0';
        }
    };
    setInterval(() => blinker.tick(), 500);

    /**
     * 将 HTML 字符串 token 化为标签与字符序列
     * 标签整体输出（保留 <a>/<code>/<br> 等结构），字符逐字打字
     * @param {string} html
     * @returns {Array<{type: string, value: string}>}
     */
    function tokenize(html) {
        const tokens = [];
        const regex = /(<[^>]+>)|([^<]+)/g;
        let match;
        while ((match = regex.exec(html)) !== null) {
            if (match[1]) {
                tokens.push({ type: 'tag', value: match[1] });
            } else if (match[2]) {
                // 折叠连续空白为单个空格，与 HTML 解析行为一致
                // 避免 textNode 逐字插入时多余空格撑宽行尾导致换行
                const text = match[2].replace(/\s+/g, ' ');
                for (const ch of text) {
                    tokens.push({ type: 'char', value: ch });
                }
            }
        }
        return tokens;
    }

    /**
     * Typewriter — 单个元素的打字机动画
     * @param {HTMLElement} el 目标元素（动画层）
     */
    class Typewriter {
        constructor(el, options = {}) {
            this.el = el;
            this.speed = options.speed || 28;
            this.tokens = [];
            this.index = 0;
            this.timer = null;
            this.resolve = null;
        }

        /**
         * 启动打字动画
         * @param {string} html 原始 innerHTML
         * @returns {Promise<void>}
         */
        start(html) {
            // 去除末尾空白：避免光标跟随在行尾空格后被挤到下一行独占
            this.tokens = tokenize(html.replace(/\s+$/, ''));
            this.index = 0;
            this.el.innerHTML = '';
            this.el.classList.remove('is-done');
            this.el.classList.add('is-typing');
            // 稳定的光标元素：始终作为最后一个子节点，内容在它之前追加
            // 避免每次打字重建光标导致闪烁不同步
            this.caret = document.createElement('span');
            this.caret.className = 'typewriter__caret';
            this.el.appendChild(this.caret);
            // 注册到全局 blinker，与所有光标同步闪烁
            blinker.register(this.caret);
            this.update();
            return new Promise((resolve) => { this.resolve = resolve; });
        }

        update() {
            if (this.index >= this.tokens.length) {
                this.el.classList.remove('is-typing');
                this.el.classList.add('is-done');
                if (this.resolve) this.resolve();
                return;
            }
            const token = this.tokens[this.index];
            this.index++;
            // 在光标前插入当前 token，光标自动跟随在内容末尾
            if (token.type === 'tag') {
                this.caret.insertAdjacentHTML('beforebegin', token.value);
            } else {
                this.el.insertBefore(document.createTextNode(token.value), this.caret);
            }
            if (token.type === 'tag') {
                // 标签立即输出，不消耗打字时间
                this.update();
            } else {
                this.timer = setTimeout(() => this.update(), this.speed);
            }
        }
    }

    /**
     * 初始化所有 .typewriter 元素
     * - 首屏：延迟触发打字，制造层次感
     * - 屏外：滚动到视口时触发
     */
    function init() {
        const targets = Array.from(document.querySelectorAll('.typewriter'));
        if (!targets.length) return;

        // 注册 nav__prompt 到全局 blinker，与所有 typewriter 光标同步闪烁
        const navPrompt = document.querySelector('.nav__prompt');
        if (navPrompt) blinker.register(navPrompt);

        const map = new WeakMap();

        targets.forEach((el) => {
            const originalHtml = el.innerHTML;
            // ghost 层渲染最终内容撑住布局，active 层叠加其上播放打字动画
            const ghost = document.createElement('span');
            ghost.className = 'typewriter__ghost';
            ghost.setAttribute('aria-hidden', 'true');
            ghost.innerHTML = originalHtml;
            const active = document.createElement('span');
            active.className = 'typewriter__active';
            active.setAttribute('aria-hidden', 'true');
            el.innerHTML = '';
            ghost.appendChild(active);
            el.appendChild(ghost);
            map.set(el, { tw: new Typewriter(active), html: originalHtml, played: false });
        });

        const viewportH = window.innerHeight;

        // 首屏可见元素立即触发
        targets.forEach((el) => {
            const rect = el.getBoundingClientRect();
            if (rect.top < viewportH) {
                const data = map.get(el);
                const delay = Math.min(rect.top / viewportH, 1) * 800 + 300;
                setTimeout(() => data.tw.start(data.html), delay);
                data.played = true;
            }
        });

        // 屏外元素滚动到视口时触发
        if ('IntersectionObserver' in window) {
            const observer = new IntersectionObserver((entries) => {
                entries.forEach((entry) => {
                    if (!entry.isIntersecting) return;
                    const el = entry.target;
                    const data = map.get(el);
                    if (!data || data.played) return;
                    data.played = true;
                    data.tw.start(data.html);
                    observer.unobserve(el);
                });
            }, { threshold: 0.2 });

            targets.forEach((el) => {
                const data = map.get(el);
                if (!data.played) observer.observe(el);
            });
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
