"use strict";

const tabs = Array.from(document.querySelectorAll('[role="tab"]'));

function activateTab(tab) {
    for (const candidate of tabs) {
        const selected = candidate === tab;
        candidate.setAttribute("aria-selected", String(selected));
        candidate.tabIndex = selected ? 0 : -1;
        document.getElementById(candidate.getAttribute("aria-controls")).hidden = !selected;
    }
}

for (const tab of tabs) {
    tab.addEventListener("click", () => activateTab(tab));
    tab.addEventListener("keydown", (event) => {
        const index = tabs.indexOf(tab);
        let nextIndex;
        if (event.key === "ArrowRight") nextIndex = (index + 1) % tabs.length;
        else if (event.key === "ArrowLeft") nextIndex = (index - 1 + tabs.length) % tabs.length;
        else if (event.key === "Home") nextIndex = 0;
        else if (event.key === "End") nextIndex = tabs.length - 1;
        else return;
        event.preventDefault();
        activateTab(tabs[nextIndex]);
        tabs[nextIndex].focus();
    });
}

const copyButton = document.querySelector("[data-copy]");
copyButton.addEventListener("click", async () => {
    const code = document.getElementById(copyButton.dataset.copy);
    const status = document.querySelector(".copy-status");
    try {
        await navigator.clipboard.writeText(code.textContent.trim());
        status.textContent = "Commands copied. Replace C:/code/my-project with your repository path.";
    } catch {
        const selection = window.getSelection();
        const range = document.createRange();
        range.selectNodeContents(code);
        selection.removeAllRanges();
        selection.addRange(range);
        status.textContent = "Select and copy the highlighted commands using your browser.";
    }
});
