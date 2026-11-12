'use strict';
/* O xterm.js cria elementos <style> em tempo de execução (dimensões e cores do renderizador de DOM).
   Em vez de liberar 'unsafe-inline', a CSP aceita só <style> com este nonce, e só este arquivo o aplica,
   a <style> criados por document.createElement. Scripts continuam restritos a 'self'. */
(() => {
  const NONCE = 'monitoramento-xterm-style-1';
  const create = Document.prototype.createElement;
  Document.prototype.createElement = function createElement(tag, options) {
    const el = create.call(this, tag, options);
    if (typeof tag === 'string' && tag.toLowerCase() === 'style') el.nonce = NONCE;
    return el;
  };
})();
