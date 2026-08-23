import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, Type, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { AuthServico } from '../nucleo/servicos/auth.servico';
import { RealtimeServico } from '../nucleo/servicos/realtime.servico';
import { Caixa } from './caixa/caixa';
import {
  CORPO, LARGURA_CELULAR, RESPONDEM_ARRAY, RealtimeFalso, TELAS, rotaFalsa
} from './telas-do-painel';

/** ===================== NENHUMA TELA ANDA DE LADO EM 390px =====================
 *  Este é o teste que substitui "abri no celular e pareceu ok". Cada tela é montada numa caixa de
 *  390px e o navegador MEDE: `scrollWidth` maior que `clientWidth` significa conteúdo fora da área
 *  visível, e o sintoma no aparelho é a tela inteira andando de lado a cada toque.
 *
 *  A causa costuma ser sempre a mesma: tabela larga, grade de colunas fixas, ou um `min-width`
 *  esquecido. Por isso a tabela vai dentro de `.tabela-rolagem`, que rola sozinha.
 *
 *  ===================== O QUE MUDOU EM RELAÇÃO À VERSÃO ANTERIOR (MOB-2) =====================
 *  Esta medição existia em `paginas.render.spec.ts` e media o que não dizia medir: a janela do
 *  karma era 1440px, e media query responde à JANELA. O que estava sendo espremido em 380px era o
 *  layout de DESKTOP — e a caixa de entrada precisava ser ISENTA da medição justamente porque em
 *  ≤860px ela monta outro layout. Foi ali que ela quebrou, com a isenção registrada em comentário.
 *
 *  Agora a janela está abaixo do ponto de quebra (ver karma.conf.js e janela.celular.spec.ts), o
 *  layout montado JÁ é o de celular, e a caixa de 390px só decide a largura em que ele é medido.
 *  Não há isenção: as 21 telas entram, a caixa de entrada inclusive, nas suas duas vistas.
 *  ============================================================================================ */
describe('nenhuma tela transborda em 390px', () => {
  let http: HttpTestingController;
  let caixa: HTMLElement;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '**', component: Vazio }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso },
        { provide: ActivatedRoute, useValue: rotaFalsa() }
      ]
    });

    http = TestBed.inject(HttpTestingController);
    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: { id: 1, nome: 'Ana Souza', email: 'ana@x.com', papel: 'dono', empresaNome: 'Padaria' }
    } as never);

    caixa = document.createElement('div');
    caixa.style.width = `${LARGURA_CELULAR}px`;
    caixa.style.overflow = 'hidden';
    document.body.appendChild(caixa);
  });

  afterEach(() => {
    caixa.remove();
    localStorage.clear();
    TestBed.resetTestingModule();
  });

  function responderTudo() {
    for (let volta = 0; volta < 5; volta++) {
      const pendentes = http.match(() => true);
      if (pendentes.length === 0) return;
      pendentes.forEach(r =>
        r.flush(RESPONDEM_ARRAY.some(u => r.request.url.includes(u)) ? [] : CORPO));
    }
  }

  function montar(componente: Type<unknown>) {
    const fixture = TestBed.createComponent(componente);
    caixa.appendChild(fixture.nativeElement);
    fixture.detectChanges();
    responderTudo();
    fixture.detectChanges();
    return fixture;
  }

  /** A margem de 1px absorve arredondamento de subpixel — sem ela o teste ficaria intermitente
   *  por diferença de fração de pixel entre execuções. */
  function exigirQueCaiba(nome: string) {
    const excesso = caixa.scrollWidth - caixa.clientWidth;
    expect(excesso)
      .withContext(
        `${nome} passa ${excesso}px de ${LARGURA_CELULAR}px — no celular a tela anda de lado`)
      .toBeLessThanOrEqual(1);
  }

  for (const tela of TELAS) {
    it(`${tela.nome} cabe`, () => {
      montar(tela.componente);
      exigirQueCaiba(tela.nome);
    });
  }

  /** ===================== NADA FLUTUA POR CIMA DO CONTEÚDO (MOB-5) =====================
   *  A barra inferior foi resolvida ficando no FLUXO, e não `position: fixed` — assim ela encolhe a
   *  área de conteúdo em vez de cobri-la, e nenhuma tela precisa reservar espaço para ela. As
   *  faixas de topo (abas, busca, filtros) seguem a mesma disciplina.
   *
   *  Este teste trava a disciplina inteira: elemento posicionado sobre o conteúdo obriga CADA tela
   *  a compensar a altura dele, e a que ninguém lembrar de compensar nasce com uma linha coberta.
   *
   *  ⚠️ DUAS EXCEÇÕES, E AS DUAS SÃO LEGÍTIMAS — flutuar É o comportamento delas:
   *
   *      .overlay   o modal. Cobre tudo porque é isso que um modal faz, e enquanto está aberto
   *                 não há conteúdo para ler atrás.
   *      .pilha     a pilha de toasts. Aviso transitório, `role="status"`, que some sozinho.
   *
   *  A lista é EXPLÍCITA de propósito: uma terceira exceção tem que ser uma decisão visível, e não
   *  um seletor a mais numa condição que ninguém relê.
   *  ==================================================================================== */
  const FLUTUAM_DE_PROPOSITO = '.overlay, .pilha';

  for (const tela of TELAS) {
    it(`${tela.nome} não põe nada flutuando sobre o conteúdo`, () => {
      const fixture = montar(tela.componente);
      const raiz = fixture.nativeElement as HTMLElement;

      const flutuantes = [...raiz.querySelectorAll('*')]
        .filter(e => !e.closest(FLUTUAM_DE_PROPOSITO))
        .filter(e => ['fixed', 'sticky'].includes(getComputedStyle(e).position))
        .map(e => `${e.tagName.toLowerCase()}.${[...e.classList].join('.')}`);

      expect(flutuantes)
        .withContext(`${tela.nome} tem elemento posicionado sobre o conteúdo — cada tela passa a ` +
                     'precisar reservar a altura dele, e a que esquecer nasce com uma linha coberta')
        .toEqual([]);
    });
  }

  /** A caixa de entrada tem DUAS vistas no celular, e o laço acima só exercita a primeira (sem
   *  conversa selecionada, a vista é a lista). A conversa aberta é justamente o layout que ficou
   *  anos sem teste — e o que quebrou. */
  it('Caixa de entrada cabe TAMBÉM com a conversa aberta', async () => {
    // O corpo genérico devolve lista vazia, e sem conversa não há o que abrir. Aqui a primeira
    // resposta é específica de propósito.
    const fixture = TestBed.createComponent(Caixa);
    caixa.appendChild(fixture.nativeElement);
    fixture.detectChanges();

    http.expectOne(r => r.url.endsWith('/conversas') && r.method === 'GET').flush({
      itens: [{
        id: 42, contatoId: 7, contatoNome: 'Marcos Antunes', telefone: '5584988887777',
        ultimaMensagemPrevia: 'tenho interesse no orçamento', ultimaMensagemDirecao: 'entrada',
        ultimaMensagemEm: '2026-08-05T12:00:00Z', aguardandoDesde: '2026-08-05T12:00:00Z',
        naoLidas: 3, status: 'aberta', responsavelId: null, responsavelNome: null,
        etapaId: 1, etapaNome: 'Novo Lead', contatoGanhou: false, canalDoCiclo: null,
        vendasEmAberto: 0
      }],
      temMais: false
    });
    responderTudo();
    fixture.detectChanges();

    const raiz = fixture.nativeElement as HTMLElement;

    const item = raiz.querySelector('.item') as HTMLButtonElement | null;
    expect(item).withContext('a lista não desenhou conversa nenhuma para abrir').toBeTruthy();

    item!.click();
    await fixture.whenStable();
    fixture.detectChanges();
    responderTudo();
    fixture.detectChanges();

    expect(raiz.querySelector('app-thread'))
      .withContext('a conversa não abriu — o resto desta medição não significa nada').not.toBeNull();
    exigirQueCaiba('Caixa de entrada (conversa aberta)');
  });
});

@Component({ template: '' })
class Vazio { }
