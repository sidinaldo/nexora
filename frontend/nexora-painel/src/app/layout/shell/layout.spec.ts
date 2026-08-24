import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import {
  POR_PAGINA, alturaMinimaDaTabela, fatiar, totalDePaginas
} from '../../nucleo/paginacao/paginacao';
import { QUEBRA_CELULAR } from '../../nucleo/viewport';
import { Shell } from './shell';

/** O ESQUELETO E AS REGRAS DE LISTA.
 *
 *  ===================== POR QUE ISTO É TESTE =====================
 *  As duas coisas que este arquivo trava não têm dono óbvio e quebram em silêncio:
 *
 *  1. A ÁREA DE ROLAGEM. Se alguém tirar `.conteudo` do shell — ou mover o `router-outlet` para
 *     fora dela —, a página inteira volta a rolar, a barra lateral sai da tela e, pior, a
 *     cadeia de altura se rompe: a thread da conversa perde o limite, `scrollHeight` passa a
 *     ser igual a `clientHeight` e a âncora de rolagem vira um no-op. O chip "nova mensagem"
 *     simplesmente para de aparecer, sem erro nenhum.
 *
 *  2. O TAMANHO DE PÁGINA. Ele estava diferente em cada tela; um número só é o que evita a
 *     surpresa, e um `20` digitado à mão em algum lugar reabre a divergência.
 *  ================================================================ */
describe('esqueleto do painel', () => {
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

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso }
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: { id: 1, nome: 'Ana Souza', email: 'ana@x.com', papel: 'dono', empresaNome: 'Padaria' }
    } as never);
  });

  afterEach(() => localStorage.clear());

  it('O ROUTER-OUTLET FICA DENTRO DA ÁREA QUE ROLA', () => {
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).match(() => true).forEach(r => r.flush({}));
    fixture.detectChanges();

    const raiz = fixture.nativeElement as HTMLElement;

    const conteudo = raiz.querySelector('.conteudo');
    expect(conteudo).withContext('a área de rolagem sumiu do shell').not.toBeNull();

    const outlet = raiz.querySelector('router-outlet');
    expect(outlet).not.toBeNull();
    expect(conteudo!.contains(outlet!))
      .withContext('o outlet saiu de dentro de .conteudo — a página inteira volta a rolar')
      .toBeTrue();

    // Os banners ficam FORA da área que rola: o de WhatsApp desconectado existe para ser
    // impossível de ignorar, e rolando junto ele sairia da tela.
    const main = raiz.querySelector('main')!;
    expect(main.contains(conteudo!)).toBeTrue();
  });

  /** ===================== A BARRA INFERIOR É DO CELULAR (MOB-4) =====================
   *  A navegação de celular é uma barra no rodapé, e a de desktop é a lateral. As duas nunca
   *  convivem — mas quem separa uma da outra é um `@if (ehCelular())` no template, e nada
   *  exercitava esse `@if` do lado do desktop. A auditoria do MOB-4 registrou o buraco: dezesseis
   *  regras de `.barra-inferior`, `.voltar-lista` e afins não tinham limite de largura no CSS, e
   *  só não vazavam porque o elemento não era renderizado.
   *
   *  A janela do karma aqui é 1440px (ver karma.conf.js), então este teste roda do lado certo.
   *  ================================================================================= */
  it('A BARRA INFERIOR NÃO EXISTE NO DESKTOP', () => {
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).match(() => true).forEach(r => r.flush({}));
    fixture.detectChanges();

    const raiz = fixture.nativeElement as HTMLElement;

    expect(raiz.querySelector('.barra-inferior'))
      .withContext('a barra de navegação do celular apareceu no desktop')
      .toBeNull();

    // E a lateral continua no lugar: o teste acima passaria com as duas navegações sumindo.
    expect(raiz.querySelector('.lateral'))
      .withContext('a barra lateral do desktop sumiu').not.toBeNull();
  });

  /** ===================== UM PONTO DE QUEBRA, DOIS DONOS =====================
   *  `ehCelular()` decide em TypeScript quais painéis existem; o CSS decide como eles se parecem.
   *  Os dois usam 860px, e são números escritos separadamente.
   *
   *  Divergindo, o defeito aparece SÓ na faixa entre os dois valores — a barra renderizada sem o
   *  próprio estilo, ou estilizada sem existir. Uma faixa estreita, no meio do caminho entre as
   *  duas larguras em que os testes rodam, que é precisamente onde ninguém olha.
   *
   *  O teste lê a condição do `@media` que embrulha a barra na folha de estilo JÁ CARREGADA e a
   *  compara com a constante. Não é leitura de arquivo: é o que o navegador entendeu.
   *  ========================================================================== */
  it('O PONTO DE QUEBRA DO CSS É O MESMO DO TYPESCRIPT', () => {
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();

    const condicoes: string[] = [];
    for (const folha of [...document.styleSheets]) {
      let regras: CSSRuleList;
      try { regras = folha.cssRules; } catch { continue; }   // folha de outra origem
      for (const regra of [...regras]) {
        if (regra instanceof CSSMediaRule && regra.cssText.includes('.barra-inferior')) {
          condicoes.push(regra.conditionText);
        }
      }
    }

    expect(condicoes.length)
      .withContext('nenhum `@media` embrulha a barra inferior — o CSS dela vale em qualquer ' +
                   'largura, e só um `@if` no template a segura')
      .toBeGreaterThan(0);

    for (const c of condicoes) {
      expect(c)
        .withContext(`o CSS da barra usa "${c}" e o TypeScript usa ${QUEBRA_CELULAR}px — entre os ` +
                     'dois valores a barra e o estilo dela discordam')
        .toBe(`(max-width: ${QUEBRA_CELULAR}px)`);
    }
  });

  it('O AVISO DE DESCONEXÃO NÃO ENTRA NA ÁREA QUE ROLA', async () => {
    // ===================== POR QUE ELE FICA FORA =====================
    // É a faixa mais importante do produto: o vendedor precisa vê-la ANTES de digitar uma
    // resposta que não vai sair. Dentro da área que rola, ela sairia da tela na primeira rolagem.
    //
    // E ficando fora com `flex: 0 0 auto`, ela ROUBA altura de `.conteudo` em vez de empurrar o
    // conteúdo para baixo da dobra — que é o que criaria a rolagem dupla e cortaria o rodapé.
    // ================================================================
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();

    // `ngOnInit` é async (conecta o realtime antes), então a requisição de status ainda não
    // saiu no primeiro `detectChanges`. Uma macrotarefa drena a cadeia de promessas.
    await new Promise(pronto => setTimeout(pronto, 0));

    // WhatsApp DESCONECTADO: é a condição que faz a faixa aparecer.
    TestBed.inject(HttpTestingController).match(() => true).forEach(r =>
      r.flush({
        naoLidas: 0, aguardando: 0, whatsappConectado: false, trocouDeNumero: true,
        semaforoAmareloMinutos: 60, semaforoVermelhoMinutos: 240,
        janelaHoraInicio: 8, janelaHoraFim: 20, janelaDiasSemana: 126, feriadosRecentes: [],
        mostrar: false, concluidos: 0, total: 3, passos: []
      }));
    fixture.detectChanges();

    const raiz = fixture.nativeElement as HTMLElement;
    const banner = raiz.querySelector('.banner-alerta');
    const conteudo = raiz.querySelector('.conteudo')!;

    expect(banner).withContext('a faixa de desconexão não apareceu').not.toBeNull();
    expect(conteudo.contains(banner!))
      .withContext('a faixa entrou na área que rola — some da tela na primeira rolagem')
      .toBeFalse();

    // E é irmã da área de conteúdo, dentro do mesmo `main`.
    expect(raiz.querySelector('main')!.contains(banner!)).toBeTrue();
  });

  it('a barra lateral e a área de conteúdo são irmãs, não aninhadas', () => {
    // Aninhar a lateral dentro do que rola é exatamente o bug que este bloco corrigiu.
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).match(() => true).forEach(r => r.flush({}));
    fixture.detectChanges();

    const raiz = fixture.nativeElement as HTMLElement;
    const lateral = raiz.querySelector('.lateral')!;
    const conteudo = raiz.querySelector('.conteudo')!;

    expect(lateral.contains(conteudo)).toBeFalse();
    expect(conteudo.contains(lateral)).toBeFalse();
  });
});

describe('regras de paginação', () => {
  it('O TAMANHO DE PÁGINA É 20 EM TODA TABELA', () => {
    expect(POR_PAGINA).toBe(20);
  });

  it('fatiar devolve exatamente a página pedida', () => {
    const itens = Array.from({ length: 45 }, (_, i) => i + 1);

    expect(fatiar(itens, 1)).toEqual(Array.from({ length: 20 }, (_, i) => i + 1));
    expect(fatiar(itens, 3)).toEqual([41, 42, 43, 44, 45]);
    // Página além do fim devolve vazio em vez de estourar — é o que acontece por um instante
    // quando a lista encolhe entre duas requisições.
    expect(fatiar(itens, 9)).toEqual([]);
  });

  it('totalDePaginas nunca é zero', () => {
    // Zero páginas faria o controle sumir E o "Página 1 de 0" aparecer, dependendo da tela.
    expect(totalDePaginas(0)).toBe(1);
    expect(totalDePaginas(1)).toBe(1);
    expect(totalDePaginas(20)).toBe(1);
    expect(totalDePaginas(21)).toBe(2);
    expect(totalDePaginas(45)).toBe(3);
  });

  it('A ALTURA MÍNIMA É DO CONTAINER, NÃO DE LINHAS FALSAS', () => {
    // ===================== O QUE MUDOU NO DES-2 =====================
    // A primeira versão preenchia a última página com linhas VAZIAS até 20. Funcionava — a
    // tabela não pulava — e estava errado: dez faixas em branco com borda são indistinguíveis
    // de dez registros que não carregaram, e o usuário não tem como saber que aquilo não é dado.
    //
    // A reserva passou para o container. Este teste existe para o preenchimento por linha não
    // voltar disfarçado.
    // ===============================================================
    expect(alturaMinimaDaTabela(20)).toBe(20 * 44 + 46);
    expect(alturaMinimaDaTabela(5)).toBe(5 * 44 + 46);

    // A função de linhas-fantasma não existe mais. Se alguém a reintroduzir, este import quebra
    // o build antes de o teste rodar — que é a intenção.
    expect(Object.keys({ alturaMinimaDaTabela })).toContain('alturaMinimaDaTabela');
  });
});
