import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { AuthServico } from '../nucleo/servicos/auth.servico';
import { RealtimeServico } from '../nucleo/servicos/realtime.servico';

import { Comecar } from './comecar/comecar';
import { Conexao } from './conexao/conexao';
import { Configuracoes } from './configuracoes/configuracoes';
import { Conta } from './conta/conta';
import { Contatos } from './contatos/contatos';
import { Dashboard } from './dashboard/dashboard';
import { Equipe } from './equipe/equipe';
import { Etapas } from './etapas/etapas';
import { Formularios } from './formularios/formularios';
import { MeuDia } from './meu-dia/meu-dia';

/** ===================== A LARGURA, MEDIDA =====================
 *  O DES-1 deixou cada tela declarar o próprio teto e o resultado ficou inconsistente: `/contatos`
 *  usando quase toda a largura e `/configuracoes` numa coluna estreita CENTRALIZADA, com faixa
 *  morta simétrica dos dois lados.
 *
 *  Este arquivo não confere se o CSS "parece certo" — ele RENDERIZA cada tela numa caixa de
 *  largura fixa e mede as bordas. É o que transforma "está fora do padrão" num número.
 *
 *  Duas regras, e só duas:
 *    • toda tela começa no MESMO x — o recuo à esquerda é único;
 *    • densa ocupa o container inteiro; formulário é mais estreita, mas ancorada na mesma
 *      borda esquerda, e nunca centralizada.
 *  ============================================================== */
describe('largura das telas', () => {
  const LARGURA = 1400;

  const DENSAS: { nome: string; c: Type<unknown> }[] = [
    { nome: '/dashboard', c: Dashboard },
    { nome: '/contatos', c: Contatos },
    { nome: '/equipe', c: Equipe },
    { nome: '/meu-dia', c: MeuDia }
  ];

  const FORMULARIOS: { nome: string; c: Type<unknown> }[] = [
    { nome: '/configuracoes', c: Configuracoes },
    { nome: '/conta', c: Conta },
    { nome: '/conexao', c: Conexao },
    { nome: '/etapas', c: Etapas },
    { nome: '/formularios', c: Formularios },
    { nome: '/comecar', c: Comecar }
  ];

  const CORPO = {
    itens: [], temMais: false, total: 0, numeroPagina: 1, tamanho: 20,
    colunas: [], etapas: [], passos: [], acoes: [], usuarios: [], feriados: [],
    conversas: [], contatos: [], lembretes: [], series: [], atividades: [],
    funil: [], origens: [], pontos: [], concluidos: 0,
    mostrar: false, completo: false, dispensado: false,
    naoLidas: 0, whatsappConectado: true, trocouDeNumero: false,
    janelaHoraInicio: 8, janelaHoraFim: 20, janelaDiasSemana: 126, feriadosRecentes: [],
    status: 'nao_criada', nome: '', email: '', telefone: '', papel: 'dono'
  };
  const ARRAYS = ['/equipe', '/feriados', '/configuracao/fusos', '/configuracao/ufs', '/etapas', '/formularios'];

  class RealtimeFalso {
    conectado = signal(true);
    mensagemRecebida$ = new Subject<never>();
    conversaAberta$ = new Subject<never>();
    contatoCriado$ = new Subject<never>();
    statusMensagem$ = new Subject<never>();
    conexaoMudou$ = new Subject<never>();
    async conectar() { }
    desconectar() { }
  }

  let http: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              paramMap: convertToParamMap({ id: '1', token: 't' }),
              queryParamMap: convertToParamMap({}), data: {}
            }
          }
        }
      ]
    });
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: { id: 1, nome: 'Ana', email: 'a@x.com', papel: 'dono', empresaNome: 'Padaria' }
    } as never);
  });

  afterEach(() => { localStorage.clear(); TestBed.resetTestingModule(); });

  /** Renderiza a tela numa caixa de largura fixa e devolve a caixa do `.pagina`. */
  function medir(componente: Type<unknown>): DOMRect | null {
    const palco = document.createElement('div');
    palco.style.width = `${LARGURA}px`;
    document.body.appendChild(palco);

    const fixture = TestBed.createComponent(componente);
    palco.appendChild(fixture.nativeElement);
    fixture.detectChanges();

    for (let volta = 0; volta < 5; volta++) {
      const pendentes = http.match(() => true);
      if (pendentes.length === 0) break;
      pendentes.forEach(r => r.flush(ARRAYS.some(u => r.request.url.includes(u)) ? [] : CORPO));
    }
    fixture.detectChanges();

    const pagina = (fixture.nativeElement as HTMLElement).querySelector('.pagina');
    const caixa = pagina ? pagina.getBoundingClientRect() : null;
    palco.remove();
    return caixa;
  }

  it('TODAS AS TELAS COMEÇAM NO MESMO X', () => {
    // ===== A CORREÇÃO DO DES-2 =====
    // Formulário estreito é legítimo — linha larga cansa a leitura. O que estava errado era ele
    // ser CENTRALIZADO: isso cria duas faixas mortas simétricas e a tela parece ter perdido
    // conteúdo. Alinhado à esquerda, o olho começa a linha no mesmo lugar em toda tela.
    const esquerdas = new Map<string, number>();

    for (const t of [...DENSAS, ...FORMULARIOS]) {
      const caixa = medir(t.c);
      expect(caixa).withContext(`${t.nome} não tem .pagina`).not.toBeNull();
      esquerdas.set(t.nome, Math.round(caixa!.left));
    }

    const valores = [...new Set(esquerdas.values())];
    expect(valores.length)
      .withContext(`recuos diferentes: ${[...esquerdas].map(([n, x]) => `${n}=${x}`).join(', ')}`)
      .toBe(1);
  });

  it('TODAS AS TELAS TÊM A MESMA LARGURA DE CONTAINER', () => {
    for (const t of [...DENSAS, ...FORMULARIOS]) {
      const caixa = medir(t.c)!;
      expect(Math.round(caixa.width))
        .withContext(`${t.nome} não usa a largura padrão`)
        .toBe(LARGURA);
    }
  });

  it('O CARTÃO DA TELA DE FORMULÁRIO NÃO ENCOLHE', () => {
    // ===================== O DEFEITO QUE ISTO TRAVA =====================
    // A primeira tentativa limitou o cartão inteiro a 860px. Medindo, parecia certo: mesmo recuo,
    // alinhado à esquerda, sem centralização. Na tela, errado — num monitor de 1900px sobravam
    // ~700px de creme à direita, e ao lado de `/contatos` a tela parecia não ter carregado.
    //
    // O limite de leitura desceu para o texto e para o campo. O cartão segue o padrão: uma lista
    // com nome à esquerda e botões à direita é tabela, não parágrafo.
    // ====================================================================
    for (const t of FORMULARIOS) {
      const palco = document.createElement('div');
      palco.style.width = `${LARGURA}px`;
      document.body.appendChild(palco);

      const fixture = TestBed.createComponent(t.c);
      palco.appendChild(fixture.nativeElement);
      fixture.detectChanges();
      for (let volta = 0; volta < 5; volta++) {
        const pendentes = http.match(() => true);
        if (pendentes.length === 0) break;
        pendentes.forEach(r => r.flush(ARRAYS.some(u => r.request.url.includes(u)) ? [] : CORPO));
      }
      fixture.detectChanges();

      const pagina = (fixture.nativeElement as HTMLElement).querySelector('.pagina')!;

      try {
        expect(pagina.classList.contains('formulario'))
          .withContext(`${t.nome} deveria ser tela de formulário`).toBeTrue();

        const cx = pagina.getBoundingClientRect();
        const recuo = parseFloat(getComputedStyle(pagina).paddingRight);
        const bordaInterna = cx.right - recuo;

        // A pergunta certa não é "o primeiro cartão é largo" — `/conexao` é uma GRADE de cartões,
        // e ali o primeiro é uma célula. É "algum bloco de conteúdo chega na borda direita".
        // Se nenhum chega, existe vazio, e é isso que a tela mostrava.
        const blocos = [...pagina.children] as HTMLElement[];
        if (blocos.length === 0) continue;

        const maisDireita = Math.max(...blocos.map(b => b.getBoundingClientRect().right));
        const vazio = Math.round(bordaInterna - maisDireita);

        expect(vazio)
          .withContext(`${t.nome}: sobram ${vazio}px de vazio à direita do conteúdo`)
          .toBeLessThanOrEqual(1);
      } finally {
        palco.remove();
      }
    }
  });
});
