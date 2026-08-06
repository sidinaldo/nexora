import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { Shell } from './shell';

/** A BARRA LATERAL EM TRÊS ZONAS (DES-3).
 *
 *  ===================== POR QUE ISTO É TESTE, E NÃO "OLHEI E ESTAVA BOM" =====================
 *  O defeito que este bloco conserta era invisível em qualquer tela grande: a lateral só passava
 *  a rolar num notebook de 768px de altura, com o grupo de configuração aberto. Quem desenvolve
 *  em monitor de 1440 nunca viu — e quando viu, o "Sair" já estava cortado.
 *
 *  Aqui a altura é FIXADA por estilo em linha, e o navegador MEDE. É a diferença entre "acho que
 *  cabe" e "cabe em 768px com os treze itens presentes".
 *
 *  ⚠️ O estilo em linha sobrescreve o `100dvh` do `.app` de propósito: `dvh` mede a janela do
 *  karma, não a caixa do teste. Sem isso, medir "a 768px" seria medir a janela do runner.
 *  ============================================================================================ */
describe('barra lateral — três zonas, densidade e status', () => {
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

  const STATUS_OK = {
    naoLidas: 3, aguardando: 0, whatsappConectado: true, conexoesCaidas: [],
    trocouDeNumero: false, semaforoAmareloMinutos: 60, semaforoVermelhoMinutos: 240,
    janelaHoraInicio: 8, janelaHoraFim: 20, janelaDiasSemana: 126, feriadosRecentes: []
  };

  interface Opcoes {
    status?: Record<string, unknown>;
    onboarding?: Record<string, unknown>;
    empresa?: string;
    largura?: number;
  }

  let fixture: ComponentFixture<Shell>;
  let palco: HTMLElement | null = null;

  /** Monta o shell numa caixa de altura e largura declaradas, e devolve a raiz.
   *
   *  `await fixture.whenStable()` NÃO é cerimônia: o `ngOnInit` do shell é `async` e espera
   *  `realtime.conectar()` antes de pedir o status. Sem esperar, `http.match` não encontra
   *  requisição nenhuma — e a tela renderiza com os dados que nunca chegaram. */
  async function montar(altura: number, opcoes: Opcoes = {}): Promise<HTMLElement> {
    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: {
        id: 1, nome: 'Ana Souza', email: 'ana@x.com', papel: 'dono',
        empresaNome: opcoes.empresa ?? 'Padaria do Bairro'
      }
    } as never);

    palco = document.createElement('div');
    palco.style.width = `${opcoes.largura ?? 1280}px`;
    palco.style.height = `${altura}px`;
    document.body.appendChild(palco);

    fixture = TestBed.createComponent(Shell);
    palco.appendChild(fixture.nativeElement);
    fixture.detectChanges();
    await fixture.whenStable();

    const http = TestBed.inject(HttpTestingController);
    for (const r of http.match(() => true)) {
      if (r.request.url.includes('/painel/status')) r.flush({ ...STATUS_OK, ...opcoes.status });
      else if (r.request.url.includes('/onboarding')) {
        r.flush(opcoes.onboarding ?? { mostrar: false, concluidos: 3, total: 3 });
      } else r.flush({});
    }
    fixture.detectChanges();

    const raiz = fixture.nativeElement as HTMLElement;

    // A caixa manda na altura, não a janela do runner.
    (raiz.querySelector('.app') as HTMLElement).style.height = `${altura}px`;
    fixture.detectChanges();

    return raiz;
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
  });

  afterEach(() => {
    palco?.remove();
    palco = null;
    localStorage.clear();
    TestBed.resetTestingModule();
  });

  // ==================================================================== a rolagem
  it('A BARRA NÃO ROLA EM 768px DE ALTURA, COM TODOS OS ITENS', async () => {
    // ===== O DEFEITO ORIGINAL, EM NÚMERO =====
    // Onze links, o separador e o cartão de primeiros passos. Se a densidade regredir, este é o
    // teste que acusa — e acusa dizendo QUANTOS pixels sobraram do lado de fora.
    const raiz = await montar(768, {
      onboarding: { mostrar: true, concluidos: 2, total: 3 }
    });

    const meio = raiz.querySelector('.meio') as HTMLElement;
    expect(meio.querySelectorAll('nav a').length)
      .withContext('o menu perdeu itens — o teste ficaria fácil pelo motivo errado').toBe(11);
    expect(meio.querySelector('.primeiros-passos')).not.toBeNull();

    const excesso = meio.scrollHeight - meio.clientHeight;
    expect(excesso)
      .withContext(`a lista passa ${excesso}px da altura disponível em 768px`)
      .toBeLessThanOrEqual(0);
  });

  it('cabem pelo menos 14 itens da altura de um item — sobra para os próximos', async () => {
    // O alvo não é "cabe hoje", é "cabe com folga": o menu já cresceu duas vezes (Captação no
    // NAV-1, Integrações no INT-3) e vai crescer de novo.
    const raiz = await montar(768);
    const meio = raiz.querySelector('.meio') as HTMLElement;

    const item = (meio.querySelector('nav a') as HTMLElement).getBoundingClientRect().height;
    const cabem = Math.floor(meio.clientHeight / (item + 1));   // +1 = o gap

    expect(item).withContext('o item ficou alto demais').toBeLessThanOrEqual(36);
    expect(cabem).withContext(`cabem ${cabem} itens de ${item.toFixed(0)}px`).toBeGreaterThanOrEqual(14);
  });

  it('A LATERAL INTEIRA NÃO ROLA — quem rola é só o meio', async () => {
    // Era `overflow-y: auto` no elemento inteiro, e por isso o rodapé rolava junto. Se alguém
    // devolver a rolagem para a lateral, "Sair" volta a sumir.
    const raiz = await montar(500);   // altura hostil de propósito
    const lateral = raiz.querySelector('.lateral') as HTMLElement;

    expect(getComputedStyle(lateral).overflowY)
      .withContext('a lateral voltou a rolar como um todo').toBe('hidden');
    expect(getComputedStyle(raiz.querySelector('.meio') as HTMLElement).overflowY).toBe('auto');
  });

  // Um teste por altura: alturas hostis de verdade, incluindo uma em que o menu com certeza rola.
  for (const altura of [900, 768, 600, 420]) {
    it(`SAIR E O BLOCO DO USUÁRIO CONTINUAM NA TELA EM ${altura}px`, async () => {
      const raiz = await montar(altura);
      const lateral = raiz.querySelector('.lateral')!.getBoundingClientRect();

      for (const seletor of ['.usuario', '.sair']) {
        const el = raiz.querySelector(seletor)!.getBoundingClientRect();

        expect(el.height).withContext(`${seletor} sumiu`).toBeGreaterThan(0);
        expect(Math.round(el.bottom))
          .withContext(`${seletor} passou da borda de baixo da lateral`)
          .toBeLessThanOrEqual(Math.ceil(lateral.bottom));
        expect(Math.round(el.top))
          .withContext(`${seletor} começa acima da lateral`)
          .toBeGreaterThanOrEqual(Math.floor(lateral.top));
      }
    });
  }

  // ==================================================================== status da conexão
  it('O PONTO DE STATUS FICA NO ITEM "CONEXÃO", COM AS TRÊS CORES', async () => {
    // ===== O ESTADO DA COISA JUNTO DO LINK QUE LEVA ATÉ ELA =====
    // Ele informa SEMPRE. A faixa vermelha do topo alerta só no crítico — papéis distintos, não
    // duplicação.
    const raiz = await montar(900);

    const conexao = [...raiz.querySelectorAll('nav a')]
      .find(a => a.textContent?.trim().startsWith('Conexão'))!;
    const ponto = conexao.querySelector('.ponto-status') as HTMLElement;

    expect(ponto).withContext('o item Conexão ficou sem indicador').not.toBeNull();
    expect(ponto.classList.contains('ok')).withContext('conectado deveria ser "ok"').toBeTrue();
    const verde = getComputedStyle(ponto).backgroundColor;

    // O ponto tem que MUDAR com o estado — um indicador de cor fixa é decoração.
    fixture.componentInstance.status.set(null);
    fixture.detectChanges();
    expect(ponto.classList.contains('verificando')).toBeTrue();
    const ambar = getComputedStyle(ponto).backgroundColor;

    fixture.componentInstance.status.set({ ...STATUS_OK, whatsappConectado: false } as never);
    fixture.componentInstance.whatsappConectado.set(false);
    fixture.detectChanges();
    expect(ponto.classList.contains('caiu')).toBeTrue();
    const vermelho = getComputedStyle(ponto).backgroundColor;

    expect(new Set([verde, ambar, vermelho]).size)
      .withContext(`as três cores do status são iguais: ${verde} / ${ambar} / ${vermelho}`).toBe(3);
  });

  it('o ponto tem rótulo em texto — cor sozinha não conta o estado', async () => {
    const raiz = await montar(900, { status: { whatsappConectado: false, conexoesCaidas: ['Vendas'] } });

    const ponto = raiz.querySelector('.ponto-status') as HTMLElement;
    expect(ponto.title).toContain('Vendas');
  });

  it('O INDICADOR DE CONEXÃO SAIU DO RODAPÉ', async () => {
    // Ele dizia "sem conexão" para o hub de tempo real, ao lado de um banner que diz quase a mesma
    // coisa sobre o WhatsApp — dois textos parecidos para dois fatos diferentes, no lugar da tela
    // que some primeiro.
    const raiz = await montar(900);
    const rodape = raiz.querySelector('.rodape')!;

    expect(rodape.querySelector('.realtime'))
      .withContext('o indicador antigo voltou para o rodapé').toBeNull();
    expect(rodape.textContent).not.toContain('sem conexão');
    expect(rodape.textContent).not.toContain('ao vivo');

    // Mas o fato NÃO se perdeu: ele virou o pulso ao lado da marca, no topo fixo.
    const pulso = raiz.querySelector('.topo-lateral .pulso') as HTMLElement;
    expect(pulso).withContext('o estado do tempo real sumiu da tela').not.toBeNull();
    expect(pulso.title).toContain('ao vivo');
  });

  it('a faixa de desconexão continua, e não cria rolagem dupla', async () => {
    const raiz = await montar(768, {
      status: { whatsappConectado: false, conexoesCaidas: ['Principal'] }
    });

    expect(raiz.querySelector('.banner-alerta')).withContext('a faixa sumiu').not.toBeNull();
    expect(raiz.querySelector('.banner-alerta')!.textContent).toContain('Principal');

    // `main` não rola: quem encolhe para caber é `.conteudo`, que já tem a própria rolagem.
    const principal = raiz.querySelector('main') as HTMLElement;
    expect(getComputedStyle(principal).overflowY).toBe('hidden');
    expect(principal.scrollHeight).toBeLessThanOrEqual(principal.clientHeight + 1);

    // E o app inteiro continua sem rolar — a faixa não empurrou nada para fora.
    const app = raiz.querySelector('.app') as HTMLElement;
    expect(app.scrollHeight).toBeLessThanOrEqual(app.clientHeight + 1);
  });

  // ==================================================================== rodapé
  it('o bloco do usuário é compacto, clicável e leva a /conta', async () => {
    const raiz = await montar(900);

    const usuario = raiz.querySelector('.usuario') as HTMLAnchorElement;
    expect(usuario.getAttribute('href')).toBe('/conta');
    expect(usuario.textContent).toContain('Ana Souza');
    expect(usuario.textContent).toContain('dono');

    // UMA linha: antes eram três blocos empilhados (tempo real, usuário, Sair) somando ~90px.
    const rodape = (raiz.querySelector('.rodape') as HTMLElement).getBoundingClientRect();
    expect(rodape.height)
      .withContext(`o rodapé está com ${rodape.height.toFixed(0)}px — voltou a empilhar?`)
      .toBeLessThanOrEqual(64);

    // "Sair" AO LADO, não item de menu do mesmo peso que Dashboard.
    const sair = raiz.querySelector('.sair') as HTMLElement;
    const item = raiz.querySelector('nav a') as HTMLElement;
    expect(parseFloat(getComputedStyle(sair).fontSize))
      .toBeLessThan(parseFloat(getComputedStyle(item).fontSize));
  });

  it('o papel é menor e mais fraco que o nome', async () => {
    const raiz = await montar(900);
    const nome = getComputedStyle(raiz.querySelector('.dados strong')!);
    const papel = getComputedStyle(raiz.querySelector('.dados small')!);

    expect(parseFloat(papel.fontSize)).toBeLessThan(parseFloat(nome.fontSize));
    expect(papel.color).not.toBe(nome.color);
  });

  // ==================================================================== marca
  it('NOME DE EMPRESA LONGO TRUNCA, NÃO QUEBRA', async () => {
    // Sem isto, "Comércio de Materiais de Construção Silva & Filhos" vira três linhas e empurra o
    // menu inteiro para baixo — exatamente o problema que este bloco veio resolver.
    const raiz = await montar(900, {
      empresa: 'Comércio de Materiais de Construção Silva & Filhos Ltda ME'
    });

    const empresa = raiz.querySelector('.empresa') as HTMLElement;
    const estilo = getComputedStyle(empresa);

    expect(estilo.whiteSpace).toBe('nowrap');
    expect(estilo.textOverflow).toBe('ellipsis');
    expect(empresa.title).toContain('Comércio de Materiais');

    // Uma linha só: a altura não pode passar de ~1.6x a do texto.
    const linha = parseFloat(estilo.fontSize) * 1.6;
    expect(empresa.getBoundingClientRect().height)
      .withContext('o nome da empresa quebrou em mais de uma linha').toBeLessThanOrEqual(linha);
  });

  // ==================================================================== primeiros passos
  it('primeiros passos é CARTÃO e fica fora do nav', async () => {
    const raiz = await montar(900, { onboarding: { mostrar: true, concluidos: 2, total: 3 } });

    const cartao = raiz.querySelector('.primeiros-passos');
    expect(cartao).not.toBeNull();
    expect(cartao!.textContent).toContain('2/3');

    // Fora do `<nav>`: é onboarding, temporário por natureza — não é navegação permanente.
    expect(raiz.querySelector('nav .primeiros-passos'))
      .withContext('o cartão virou item de menu').toBeNull();
  });

  it('primeiros passos SOME quando completo ou dispensado', async () => {
    // `mostrar` é DERIVADO do estado no servidor (checklist cumprido) ou da decisão de dispensar.
    // Ninguém precisa lembrar de desligar nada aqui.
    const raiz = await montar(900, { onboarding: { mostrar: false, concluidos: 3, total: 3 } });
    expect(raiz.querySelector('.primeiros-passos')).toBeNull();
  });

  // ==================================================================== celular
  it('EM 380px A BARRA NÃO EMPURRA A PÁGINA DE LADO', async () => {
    // ⚠️ A media query responde à JANELA do runner (1440px, ver karma.conf.js), não a esta caixa.
    // O que se mede aqui é o layout DESKTOP espremido em 380px — e é o pior caso: se ele não
    // transborda assim, o recolhido de verdade (68px) também não.
    const raiz = await montar(700, { largura: 380 });

    const app = raiz.querySelector('.app') as HTMLElement;
    expect(app.scrollWidth - app.clientWidth)
      .withContext('a barra empurra a página de lado em 380px').toBeLessThanOrEqual(1);

    // A navegação continua alcançável, e o rodapé continua na tela.
    expect(raiz.querySelectorAll('nav a').length).toBeGreaterThan(5);
    expect((raiz.querySelector('.usuario') as HTMLElement).getBoundingClientRect().height)
      .toBeGreaterThan(0);
    expect((raiz.querySelector('.sair') as HTMLElement).getBoundingClientRect().height)
      .toBeGreaterThan(0);
  });
});
