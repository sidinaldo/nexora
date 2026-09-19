import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { ConversaResumo } from '../../nucleo/modelos';
import { RealtimeFalso, rotaFalsa } from '../telas-do-painel';
import { Caixa } from './caixa';

/** ===================== A CAIXA NO CELULAR (MOB-2) =====================
 *  O defeito que este arquivo existe para travar, escrito antes da correção:
 *
 *      caixa.css   @media (max-width: 860px) { .conversa { display: none } }
 *
 *  O toque grava `sel()`, o painel da conversa existe no DOM, e o CSS o apaga. A tela mais usada
 *  do produto vira uma lista que não abre nada — sem erro, sem aviso e sem caminho alternativo.
 *
 *  ⚠️ ESTE TESTE SÓ VALE NA JANELA DE 390px. Media query responde à JANELA do navegador, não à
 *  caixa em que o teste renderiza — foi por isso que a versão anterior da suíte não conseguia
 *  pegar isto e precisou ISENTAR a caixa da medição (`SEM_COBERTURA_A_380PX`, removido). Por
 *  isso ele é `.celular.spec.ts` e roda em `npm run test:celular`.
 *
 *  A asserção é sobre o RESULTADO — a conversa está na tela —, não sobre o mecanismo. Trocar
 *  `display: none` por renderização condicional não pode exigir reescrever o teste, senão ele
 *  estaria travando a implementação em vez do comportamento.
 *  ====================================================================== */
describe('caixa no celular — tocar num contato abre a conversa', () => {
  const CONVERSA: ConversaResumo = {
    id: 42, contatoId: 7, contatoNome: 'Marcos Antunes', telefone: '5584988887777',
    ultimaMensagemPrevia: 'tenho interesse', ultimaMensagemDirecao: 'entrada',
    ultimaMensagemEm: '2026-08-05T12:00:00Z', aguardandoDesde: '2026-08-05T12:00:00Z',
    naoLidas: 0, status: 'aberta', responsavelId: null, responsavelNome: null,
    etapaId: 1, etapaNome: 'Novo Lead', podeAbrirNegociacao: false, funisDisponiveis: [], podeRegistrarVenda: true, contatoGanhou: false, canalDoCiclo: null,
    vendasEmAberto: 0, etiquetas: []
  };

  let http: HttpTestingController;
  let palco: HTMLElement;

  /** O host da caixa é `height: 100%` — ele precisa de um pai com altura, como o `.conteudo` do
   *  shell. Sem isso a thread mediria a altura do próprio conteúdo e o teste diria pouco. */
  function montar(): ComponentFixture<Caixa> {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        // Rota coringa: `abrir()` e `voltar()` navegam de verdade para gravar `?conversa=` na
        // URL, e sem rota que case a navegação falha e a rejeição vaza para o teste.
        provideRouter([{ path: '**', component: Vazio }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso },
        { provide: ActivatedRoute, useValue: rotaFalsa({}, {}) }
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: { id: 1, nome: 'Ana', email: 'a@x.com', papel: 'dono', empresaNome: 'Padaria' }
    } as never);

    http = TestBed.inject(HttpTestingController);

    palco = document.createElement('div');
    palco.style.height = '700px';
    document.body.appendChild(palco);

    const fixture = TestBed.createComponent(Caixa);
    palco.appendChild(fixture.nativeElement);
    fixture.detectChanges();

    http.expectOne(r => r.url.endsWith('/conversas') && r.method === 'GET')
      .flush({ itens: [CONVERSA], temMais: false });

    responderPendentes();
    fixture.detectChanges();
    return fixture;
  }

  /** Drena tudo o que a tela pediu. A thread busca as mensagens assim que aparece, e uma
   *  resposta pode disparar a próxima. */
  function responderPendentes() {
    for (let volta = 0; volta < 5; volta++) {
      const pendentes = http.match(() => true);
      if (pendentes.length === 0) return;
      pendentes.forEach(r => r.flush({
        itens: [], temMais: false,
        naoLidas: 0, aguardando: 0, whatsappConectado: true, trocouDeNumero: false,
        semaforoAmareloMinutos: 60, semaforoVermelhoMinutos: 240,
        janelaHoraInicio: 8, janelaHoraFim: 20, janelaDiasSemana: 126, feriadosRecentes: []
      }));
    }
  }

  afterEach(() => {
    palco?.remove();
    localStorage.clear();
    TestBed.resetTestingModule();
  });

  it('TOCAR NUM CONTATO PÕE A CONVERSA NA TELA', async () => {
    const fixture = montar();
    const raiz = fixture.nativeElement as HTMLElement;

    const item = raiz.querySelector('.item') as HTMLButtonElement;
    expect(item).withContext('a lista não desenhou nenhuma conversa').toBeTruthy();

    item.click();
    await fixture.whenStable();
    fixture.detectChanges();
    responderPendentes();
    fixture.detectChanges();

    const thread = raiz.querySelector('app-thread') as HTMLElement | null;
    const altura = thread?.getBoundingClientRect().height ?? 0;

    expect(altura)
      .withContext(
        'a conversa foi selecionada mas não ocupa espaço nenhum na tela — no celular o vendedor ' +
        'toca no contato e nada acontece')
      .toBeGreaterThan(0);
  });

  // ==================================================================== a faixa de abas (issue #5)
  /** ===================== NENHUM FILTRO FICA ESCONDIDO =====================
   *  ⚠️ ESTE TESTE AFIRMAVA O CONTRÁRIO. Ele se chamava `..._NÃO_PERDE_ALTURA_PARA_BARRA_DE_
   *  ROLAGEM` e EXIGIA que a faixa rolasse (`scrollWidth > clientWidth`), com o contexto "as abas
   *  caberiam na tela — este teste deixou de medir o que dizia medir". Era o desenho do MOB-5:
   *  as cinco abas somam 540px, a coluna tem 340 (390 no celular), e rolar custava menos altura
   *  que quebrar linha.
   *
   *  A troca foi desfeita pelo relato "os filtros estão escondidos com scroll / precisa ficar
   *  visível". Duas das cinco viviam atrás de uma barra cinza, e quem não vê a aba não sabe em
   *  que recorte está olhando — é pior que os ~30px da segunda fileira.
   *  ======================================================================== */
  it('NENHUMA ABA FICA ESCONDIDA ATRÁS DE ROLAGEM', () => {
    const raiz = montar().nativeElement as HTMLElement;
    const faixa = raiz.querySelector('.lista-topo .abas') as HTMLElement;

    // 1. Não há o que rolar: tudo o que existe já está dentro da caixa da faixa.
    expect(faixa.scrollWidth)
      .withContext('a faixa voltou a esconder aba atrás de rolagem horizontal')
      .toBeLessThanOrEqual(faixa.clientWidth + 1);

    // 2. E cada pílula, uma por uma, está DENTRO da faixa — `scrollWidth` sozinho não pega uma
    //    aba cortada por `overflow: hidden`, que some sem nem oferecer a barra.
    const f = faixa.getBoundingClientRect();
    const abas = [...faixa.querySelectorAll('.aba')] as HTMLElement[];

    expect(abas.length).withContext('a faixa não desenhou as cinco abas').toBe(5);

    for (const aba of abas) {
      const a = aba.getBoundingClientRect();
      expect(a.left).withContext(`"${aba.textContent?.trim()}" começa à esquerda da faixa`)
        .toBeGreaterThanOrEqual(f.left - 1);
      expect(a.right).withContext(`"${aba.textContent?.trim()}" termina fora da faixa`)
        .toBeLessThanOrEqual(f.right + 1);
    }

    // 3. E a barra de rolagem não cobra altura — o motivo do teste antigo continua valendo, só
    //    que agora por não haver rolagem nenhuma em vez de por `overflow-y: hidden`.
    const roubado = Math.round(faixa.getBoundingClientRect().height - faixa.clientHeight);
    expect(roubado)
      .withContext(`a faixa gasta ${roubado}px de altura com barra de rolagem`)
      .toBeLessThanOrEqual(1);
  });

  it('A FAIXA DE ABAS NÃO COBRE O CAMPO DE BUSCA', () => {
    const raiz = montar().nativeElement as HTMLElement;
    const faixa = raiz.querySelector('.lista-topo .abas') as HTMLElement;
    const busca = raiz.querySelector('.lista-busca') as HTMLElement;

    expect(faixa.getBoundingClientRect().bottom)
      .withContext('as abas terminam depois de a busca começar — uma está por cima da outra')
      .toBeLessThanOrEqual(busca.getBoundingClientRect().top + 1);
  });

  /** ⚠️ O CASO QUE QUEBRAVA: abrir a conversa DESTRÓI a lista (é o `@if` que faz estado e DOM
   *  dizerem a mesma coisa), e voltar recriava a faixa com a rolagem zerada. Quem filtrava por
   *  "Resolvidas" — a última das cinco — voltava sem enxergar em que filtro estava.
   *
   *  ⚠️ HOJE ELE PASSA SEM ESFORÇO, e continua aqui de propósito: é a rede embaixo do teste
   *  acima. Se alguém devolver o `.rolam` à faixa para economizar altura, "Resolvidas" volta a
   *  sumir exatamente por este caminho — e é o caminho mais difícil de reproduzir a mão. */
  it('A ABA ATIVA CONTINUA VISÍVEL AO VOLTAR DA CONVERSA', async () => {
    const fixture = montar();
    const raiz = fixture.nativeElement as HTMLElement;

    const ultima = [...raiz.querySelectorAll('.aba')].at(-1) as HTMLButtonElement;
    expect(ultima.textContent!.trim()).toBe('Resolvidas');
    ultima.click();
    await fixture.whenStable();
    fixture.detectChanges();
    responderPendentes();
    fixture.detectChanges();

    (raiz.querySelector('.item') as HTMLButtonElement | null)?.click();
    await fixture.whenStable();
    fixture.detectChanges();
    responderPendentes();
    fixture.detectChanges();

    (raiz.querySelector('.voltar-lista') as HTMLButtonElement)?.click();
    await fixture.whenStable();
    fixture.detectChanges();
    responderPendentes();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const faixa = raiz.querySelector('.lista-topo .abas') as HTMLElement;
    const ativa = faixa.querySelector('.aba.ativa') as HTMLElement;
    expect(ativa).withContext('nenhuma aba está marcada como ativa').not.toBeNull();

    const f = faixa.getBoundingClientRect();
    const a = ativa.getBoundingClientRect();
    expect(a.left).withContext(`"${ativa.textContent?.trim()}" ficou à esquerda da faixa`)
      .toBeGreaterThanOrEqual(f.left - 1);
    expect(a.right).withContext(`"${ativa.textContent?.trim()}" ficou fora da tela, à direita`)
      .toBeLessThanOrEqual(f.right + 1);
  });

  it('VOLTAR devolve a lista, e a conversa sai da tela', async () => {
    const fixture = montar();
    const raiz = fixture.nativeElement as HTMLElement;

    (raiz.querySelector('.item') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();
    responderPendentes();
    fixture.detectChanges();

    const voltar = raiz.querySelector('.voltar-lista') as HTMLButtonElement | null;
    expect(voltar).withContext('não há botão de voltar — a conversa vira um beco sem saída').toBeTruthy();

    voltar!.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(raiz.querySelector('.lista'))
      .withContext('a lista não voltou').not.toBeNull();
    expect(raiz.querySelector('app-thread'))
      .withContext('a conversa continua na tela depois de voltar').toBeNull();
  });

  it('ANTES DE TOCAR, a lista ocupa a largura toda e não há conversa na tela', () => {
    const fixture = montar();
    const raiz = fixture.nativeElement as HTMLElement;

    const lista = raiz.querySelector('.lista') as HTMLElement;
    expect(Math.round(lista.getBoundingClientRect().width))
      .withContext('a lista deveria ocupar a largura inteira enquanto nenhuma conversa está aberta')
      .toBe(Math.round(palco.clientWidth));

    const thread = raiz.querySelector('app-thread');
    expect(thread).withContext('não deveria haver thread antes de escolher uma conversa').toBeNull();
  });
});

@Component({ template: '' })
class Vazio { }
