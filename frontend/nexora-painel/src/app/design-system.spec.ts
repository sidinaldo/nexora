import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { AuthServico } from './nucleo/servicos/auth.servico';
import { RealtimeServico } from './nucleo/servicos/realtime.servico';

import { Caixa } from './paginas/caixa/caixa';
import { Contatos } from './paginas/contatos/contatos';
import { Dashboard } from './paginas/dashboard/dashboard';
import { Equipe } from './paginas/equipe/equipe';
import { Formularios } from './paginas/formularios/formularios';
import { MeuDia } from './paginas/meu-dia/meu-dia';

/** ===================== O DESIGN SYSTEM NÃO PODE SE DISSOLVER =====================
 *  As primitivas de tela (`.aba`, `.avatar`, `.sub`, `.topo`) estavam duplicadas dentro de cada
 *  componente. O Angular encapsula CSS, então nada vazava e nada quebrava — mas as cópias
 *  divergiram sozinhas, uma tela por vez:
 *
 *      .aba      3 definições, 3 corpos DIFERENTES — nenhuma igual
 *      .avatar   6 definições, 4 corpos diferentes
 *      .topo    11 definições, 5 corpos diferentes
 *
 *  Ninguém aprovou que a pílula de aba do /caixa fosse diferente da do /contatos. Foi
 *  acontecendo. Esse é o custo real da duplicação: a identidade do produto se desfaz sem que
 *  ninguém decida.
 *
 *  Consolidar resolveu o passado. Este arquivo protege o futuro: ele RENDERIZA telas diferentes e
 *  compara o estilo COMPUTADO do mesmo componente visual. Uma cópia nova dentro de um componente
 *  vence o global por ordem de carga — e o teste acusa na hora.
 *  ================================================================================== */
describe('design system — as primitivas não divergem entre telas', () => {
  // Uma linha em cada lista — o avatar só existe dentro de linha, e com tudo vazio o teste
  // encontraria zero e passaria sem comparar nada.
  const CONTATO = {
    id: 1, nome: 'Marcos Antunes', telefone: '5584988887777', email: null, origem: 'whatsapp',
    responsavelId: null, responsavelNome: null, valor: null, etapaId: 1, etapaNome: 'Novo Lead',
    criadoEm: '2026-08-01T10:00:00Z', ganhoEm: null, perdidoEm: null, naoLidas: 0
  };
  const ACAO = {
    tipo: 'responder', id: 1, contatoId: 1, contatoNome: 'Marcos Antunes',
    telefone: '5584988887777', titulo: 'Responder', conversaId: 1,
    aguardandoDesde: '2026-08-05T12:00:00Z', minutosUteis: 30,
    esperaAcimaDaJanela: false, horaAlvo: null
  };

  const CORPO = {
    itens: [CONTATO], temMais: false, total: 1, numeroPagina: 1, tamanho: 20,
    colunas: [], etapas: [], passos: [], acoes: [ACAO], usuarios: [], feriados: [],
    conversas: [], contatos: [], lembretes: [], series: [], atividades: [],
    funil: [], origens: [], pontos: [], concluidos: 0,
    mostrar: false, completo: false, dispensado: false,
    naoLidas: 0, whatsappConectado: true, trocouDeNumero: false,
    semaforoAmareloMinutos: 60, semaforoVermelhoMinutos: 240,
    janelaHoraInicio: 8, janelaHoraFim: 20, janelaDiasSemana: 126, feriadosRecentes: [],
    status: 'nao_criada', nome: '', email: '', telefone: '', papel: 'dono'
  };
  const ARRAYS = ['/equipe', '/feriados', '/configuracao/', '/etapas', '/formularios'];

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
              paramMap: convertToParamMap({ id: '1' }),
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

  /** Renderiza a tela num palco anexado ao documento — `getComputedStyle` só devolve valor real
   *  para elemento que está no DOM. */
  function render(componente: Type<unknown>): { raiz: HTMLElement; limpar: () => void } {
    const palco = document.createElement('div');
    palco.style.width = '1200px';
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

    return { raiz: fixture.nativeElement as HTMLElement, limpar: () => palco.remove() };
  }

  /** As propriedades que definem a APARÊNCIA do componente — não a posição dele na tela. */
  function assinatura(el: Element, props: string[]): string {
    const s = getComputedStyle(el);
    return props.map(p => `${p}=${s.getPropertyValue(p)}`).join(' | ');
  }

  function coletar(telas: { nome: string; c: Type<unknown> }[], seletor: string, props: string[]) {
    const achados = new Map<string, string>();
    for (const t of telas) {
      const { raiz, limpar } = render(t.c);
      try {
        const el = raiz.querySelector(seletor);
        if (el) achados.set(t.nome, assinatura(el, props));
      } finally {
        limpar();
      }
    }
    return achados;
  }

  it('A PÍLULA DE ABA É A MESMA EM TODA TELA', () => {
    // Era o pior caso: três definições, três corpos, nenhuma igual. Padding, borda e transição
    // diferentes em /caixa, /contatos e /formularios.
    const achados = coletar(
      [{ nome: '/caixa', c: Caixa }, { nome: '/contatos', c: Contatos }, { nome: '/formularios', c: Formularios }],
      '.aba',
      ['padding-top', 'padding-right', 'padding-bottom', 'padding-left',
       'border-top-width', 'border-radius', 'font-size', 'background-color', 'color']);

    expect(achados.size).withContext('nenhuma tela renderizou uma .aba').toBeGreaterThan(1);

    const distintas = new Set(achados.values());
    expect(distintas.size)
      .withContext(`abas diferentes:\n${[...achados].map(([n, a]) => `  ${n}: ${a}`).join('\n')}`)
      .toBe(1);
  });

  it('O AVATAR É O MESMO EM TODA TELA DE CONTEÚDO', () => {
    // O da barra lateral fica de fora de propósito: ele é branco sobre verde escuro, e o
    // contraste é o que muda — não dá para resolver com modificador de tamanho.
    const achados = coletar(
      [{ nome: '/contatos', c: Contatos }, { nome: '/meu-dia', c: MeuDia }],
      '.avatar',
      ['width', 'height', 'border-radius', 'background-color', 'color', 'font-size', 'font-weight']);

    expect(achados.size).toBeGreaterThan(1);
    expect(new Set(achados.values()).size)
      .withContext(`avatares diferentes:\n${[...achados].map(([n, a]) => `  ${n}: ${a}`).join('\n')}`)
      .toBe(1);
  });

  it('O SUBTÍTULO DE TELA É O MESMO EM TODA TELA DO PAINEL', () => {
    // 15 definições, 3 corpos. O da tela pública tem margem embaixo — mas essa fica escopada por
    // `.tela-centro`, então as telas do painel têm que bater entre si.
    const achados = coletar(
      [{ nome: '/contatos', c: Contatos }, { nome: '/dashboard', c: Dashboard },
       { nome: '/equipe', c: Equipe }, { nome: '/meu-dia', c: MeuDia }],
      '.sub',
      ['font-size', 'margin-top', 'margin-bottom']);

    expect(achados.size).toBeGreaterThan(2);
    expect(new Set(achados.values()).size)
      .withContext(`subtítulos diferentes:\n${[...achados].map(([n, a]) => `  ${n}: ${a}`).join('\n')}`)
      .toBe(1);
  });
});
