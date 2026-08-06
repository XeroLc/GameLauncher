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
