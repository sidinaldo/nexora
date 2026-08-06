import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { AuthServico } from './nucleo/servicos/auth.servico';
import { RealtimeServico } from './nucleo/servicos/realtime.servico';

import { Caixa } from './paginas/caixa/caixa';
import { Conexao } from './paginas/conexao/conexao';
import { Configuracoes } from './paginas/configuracoes/configuracoes';
import { Contato } from './paginas/contato/contato';
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

  /** `preparar` existe porque nem todo componente visual está visível no estado inicial da tela:
   *  a grade de dois campos do `/contatos` mora dentro do modal de cadastro. Sem abrir o modal, o
   *  teste encontraria zero elementos e passaria sem comparar nada — verde falso. */
  function coletar(
    telas: { nome: string; c: Type<unknown>; preparar?: (inst: never) => void }[],
    seletor: string, props: string[]) {
    const achados = new Map<string, string>();
    for (const t of telas) {
      const palco = document.createElement('div');
      palco.style.width = '1200px';
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

      try {
        if (t.preparar) { t.preparar(fixture.componentInstance as never); fixture.detectChanges(); }
        const el = (fixture.nativeElement as HTMLElement).querySelector(seletor);
        if (el) achados.set(t.nome, assinatura(el, props));
      } finally {
        palco.remove();
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

  it('A GRADE É A DO GLOBAL, NÃO UMA CÓPIA POR TELA', () => {
    // ===================== O QUE ISTO TRAVA =====================
    // Havia OITO grades espalhadas: três de duas colunas (`.dois`, `.secundarios`, `.colunas`) e
    // duas de quatro (`.numeros` no dashboard e na conexão), cada uma com a geometria reescrita —
    // e com pontos de quebra que já divergiam (520px na conexão contra 620px no resto).
    //
    // O teste compara o `grid-template-columns` COMPUTADO da mesma classe em telas diferentes.
    // Uma cópia nova dentro de um componente vence o global por ordem de carga, e cai aqui.
    // ============================================================
    // `.grade-2` sem modificador e `.grade-2.blocos` PRECISAM diferir na quebra — é o motivo do
    // modificador existir. O que não pode é a GEOMETRIA BASE divergir entre telas.
    //
    // O `/contatos` guarda a grade dentro do modal de cadastro; sem abri-lo, o teste não
    // encontraria nada e passaria por engano.
    // `/contato` ficou de FORA: o formulário de edição dele só abre com `dados()` carregado, e o
    // payload genérico deste arquivo não monta o contato inteiro. Numa lista maior ele sumiria em
    // silêncio — e foi exatamente assim que a mutação passou batido antes da asserção abaixo.
    const dois = coletar(
      [
        { nome: '/configuracoes', c: Configuracoes },
        { nome: '/contatos', c: Contatos, preparar: (i: never) => (i as unknown as Contatos).abrirNovo() }
      ],
      '.grade-2',
      ['grid-template-columns', 'gap']);

    // ===== TODAS as telas listadas TÊM que contribuir =====
    // `toBeGreaterThan(1)` deixava o teste passar comparando 2 de 3: uma tela cujo elemento não
    // renderizou some da comparação em silêncio, e uma cópia divergente justamente nela passa
    // batido. Descobri isso por mutação — o teste passou com `.grade-2` de três colunas no
    // /contato. Exigir a lista inteira é o que fecha o buraco.
    expect([...dois.keys()].sort())
      .withContext('alguma tela não renderizou .grade-2 — a comparação ficaria incompleta')
      .toEqual(['/configuracoes', '/contatos']);

    // ===== COMPARAR COLUNAS E GAP, NÃO PIXELS =====
    // O `grid-template-columns` COMPUTADO resolve `1fr` em pixels reais, e os containers têm
    // larguras diferentes — a grade do cartão de configurações mede 545px por coluna, a do modal
    // de contatos mede 204px. Comparar o valor bruto reprovaria sempre, sem nada de errado.
    // O que precisa bater é a FORMA: quantas colunas e qual o espaçamento.
    const forma = [...dois].map(([tela, a]) => {
      const cols = /grid-template-columns=([^|]*)/.exec(a)![1].trim().split(/\s+/).length;
      const gap = /gap=([^|]*)/.exec(a)![1].trim();
      return { tela, assinatura: `${cols} colunas · gap ${gap}` };
    });

    expect(new Set(forma.map(f => f.assinatura)).size)
      .withContext(`grade-2 com forma diferente:\n${forma.map(f => `  ${f.tela}: ${f.assinatura}`).join('\n')}`)
      .toBe(1);
    expect(forma[0].assinatura).toBe('2 colunas · gap 12px');
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
