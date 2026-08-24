/** ===================== A GUARDA DA SUÍTE DE CELULAR (MOB-2) =====================
 *  Tudo o que os `*.celular.spec.ts` medem depende de duas premissas do LANÇAMENTO do navegador:
 *  a janela estar abaixo do ponto de quebra do produto, e o ponteiro ser grosso. Media query
 *  responde à janela e ao dispositivo, não ao elemento — se o launcher não aplicar as flags,
 *  cada suíte de celular passa a medir o layout de DESKTOP e continua VERDE, porque nenhuma
 *  delas afirma a própria premissa.
 *
 *  É exatamente o modo de falha que este bloco existe para consertar: a caixa de entrada quebrou
 *  no celular porque o único teste que chegava perto media o desktop e dizia isso em comentário.
 *  Um comentário não reprova build.
 *
 *  Este arquivo é a asserção que faltava. Vermelho aqui significa "ignore o resto da execução".
 *  ================================================================================ */
describe('a janela desta execução é de celular', () => {
  /** O ponto de quebra do painel: shell, caixa de entrada, tabelas e campos mudam aqui. */
  const QUEBRA = 860;

  it('a janela está abaixo do ponto de quebra do produto', () => {
    // ⚠️ NÃO se afirma 390px, e o motivo está medido em karma.conf.js: o Chrome headless trava a
    // janela em ~504px e devolve o mesmo valor pedindo 360 ou 500. O que importa aqui não é o
    // número exato — é a janela montar o layout de CELULAR. A largura-alvo de 390px é aplicada
    // pela caixa em que cada suíte renderiza.
    expect(window.innerWidth)
      .withContext(
        `a janela tem ${window.innerWidth}px, acima do ponto de quebra de ${QUEBRA}px. As suítes ` +
        `de celular medem media query, que responde à JANELA — nesta largura elas estão medindo ` +
        `o layout de desktop e o verde delas não significa nada. Rode "npm run test:celular".`)
      .toBeLessThan(QUEBRA);
  });

  it('a media query de celular do produto está ativa', () => {
    // Afirmar a largura não basta: o que decide o layout é o que o CSS enxerga.
    expect(window.matchMedia(`(max-width: ${QUEBRA}px)`).matches)
      .withContext('a media query de 860px não está ativa — o CSS de celular não está valendo')
      .toBeTrue();
  });

  it('o ponteiro é GROSSO — as regras de alvo de toque estão valendo', () => {
    // Sem isto, `@media (pointer: coarse)` não casa e as regras de alvo de 44px ficam de fora:
    // o teste de toque mediria o layout de mouse e passaria. Vem de `--blink-settings` no
    // karma.conf.js.
    expect(window.matchMedia('(pointer: coarse)').matches)
      .withContext('o ponteiro está "fine" — as regras de toque não estão sendo aplicadas')
      .toBeTrue();
  });
});
