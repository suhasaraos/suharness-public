export function scrollToEnd(element) {
    if (!element) return;
    requestAnimationFrame(() => {
        element.scrollLeft = 0;
        element.scrollTop = element.scrollHeight;
    });
}

export function downloadText(fileName, content, contentType) {
    const blob = new Blob([content], { type: contentType || "text/plain" });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 0);
}