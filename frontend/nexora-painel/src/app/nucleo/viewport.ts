import { Signal, signal } from '@angular/core';

/** O ponto de quebra do painel, num lugar só.
 *
 *  860px é o mesmo número que o `styles.css`, o shell e a caixa de entrada usam. Ele existe aqui
 *  porque duas telas precisam decidir ESTRUTURA — quais painéis existem —, e isso não dá para
 *  fazer em CSS sem esconder elemento. */
export const QUEBRA_CELULAR = 860;

/** ===================== POR QUE ESTE SINAL EXISTE (MOB-2) =====================
 *  A caixa de entrada escondia o painel da conversa com `@media (max-width: 860px) { .conversa {
 *  display: none } }`. O toque gravava a seleção, o painel continuava no DOM, e o CSS o apagava:
 *  o estado dizia "conversa aberta" e a tela dizia "nada aqui".
 *
 *  Esconder por CSS é o que produz essa divergência. Com o ponto de quebra num SINAL, quem decide
 *  é o template — o painel que não está na vista simplesmente NÃO É RENDERIZADO, e DOM e estado
 *  passam a dizer a mesma coisa. De quebra, o celular deixa de montar treze links de menu e uma
 *  lista inteira fora da tela.
 *
 *  ⚠️ ISTO NÃO SUBSTITUI MEDIA QUERY. Aparência continua sendo CSS — cor, espaçamento, tamanho de
 *  campo, alvo de toque. Aqui mora só a decisão de QUAIS PAINÉIS EXISTEM, que é a única que o CSS
 *  não sabe tomar sem mentir sobre o estado.
 *
 *  Módulo, e não serviço injetável, porque é uma propriedade da JANELA e não da aplicação: não há
 *  duas respostas possíveis no mesmo instante, e passar por DI só acrescentaria cerimônia. Nos
 *  testes ele responde de verdade — a execução de celular roda numa janela abaixo do ponto de
 *  quebra (ver karma.conf.js), então o caminho exercitado é o real.
 *  ============================================================================== */
const consulta: MediaQueryList | null =
  typeof window !== 'undefined' && typeof window.matchMedia === 'function'
    ? window.matchMedia(`(max-width: ${QUEBRA_CELULAR}px)`)
    : null;

const estreita = signal(consulta?.matches ?? false);

// Rotacionar o aparelho ou abrir o painel de ferramentas troca a vista sem recarregar a página.
// Sem o listener, quem gira o celular com uma conversa aberta ficaria com o layout da orientação
// anterior até navegar.
consulta?.addEventListener('change', e => estreita.set(e.matches));

/** `true` quando a janela está em largura de celular. Somente leitura: quem escreve é a janela. */
export const ehCelular: Signal<boolean> = estreita.asReadonly();
