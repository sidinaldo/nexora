import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Captacao } from './captacao';

/** CAPTAÇÃO — a tela que juntou formulários e QR (NAV-1).
 *
 *  ===================== O QUE ESTE ARQUIVO PROTEGE =====================
 *  Três coisas que se perdem numa refatoração de navegação:
 *
 *    1. o RESUMO soma os DOIS canais. É ele que justifica as duas coisas estarem juntas — se
 *       passar a mostrar só o canal da aba aberta, a tela vira as duas de antes com um número
 *       a mais;
 *    2. a ABA vem da URL. É o que faz o link antigo de QR chegar na aba certa em vez da
 *       primeira;
 *    3. o painel da aba FECHADA não fica montado. Duas listas carregadas ao mesmo tempo custam
 *       requisição e memória (o QR vira blob) sem ninguém estar olhando.
 *  ====================================================================== */
describe('captação — as duas abas numa tela', () => {
  const FORMULARIO = {
    id: 1, nome: 'Página de contato', chave: 'abc123', dominioPermitido: null,
    ativo: true, leadsRecebidos: 30, criadoEm: '2026-08-01T10:00:00Z'
  };

  const CANAL = {
    id: 1, nome: 'Balcão da loja', codigo: 'k7m2', conexaoId: 10, conexaoNome: 'Principal',
    numero: '5584988887777', origem: 'qrcode', ativo: true, leadsRecebidos: 70,
    link: 'https://wa.me/5584988887777?text=x', texto: 'Olá! Tenho interesse. #k7m2',
    nomeArquivo: 'nexora-balcao-k7m2', podeRemover: true, motivoNaoRemove: null,
    criadoEm: '2026-08-01T10:00:00Z'
  };

  const CANAIS = { itens: [CANAL], conexoes: [], podeCriar: false, leadsAtribuidos: 70 };

  let http: HttpTestingController;
  let fixture: ComponentFixture<Captacao>;
  let c: Captacao;

  function montar(aba: string | null = null) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              paramMap: convertToParamMap({}),
              queryParamMap: convertToParamMap(aba === null ? {} : { aba }),
              data: {}
            }
          }
        }
      ]
    });

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(Captacao);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  /** O resumo pede as duas listas; a aba aberta pede a dela. Responde a TUDO que estiver
   *  pendente, que é como as outras telas deste projeto são testadas. */
  function responderTudo() {
    for (let volta = 0; volta < 4; volta++) {
      const pendentes = http.match(() => true);
      if (pendentes.length === 0) break;
      pendentes.forEach(r => r.flush(
        r.request.url.includes('/canais') ? CANAIS : [FORMULARIO]));
    }
    fixture.detectChanges();
  }

  function texto(): string { return (fixture.nativeElement as HTMLElement).textContent ?? ''; }

  afterEach(() => TestBed.resetTestingModule());

  // ==================================================================== resumo
  it('O RESUMO SOMA OS DOIS CANAIS, NÃO SÓ O DA ABA ABERTA', () => {
    montar();
    responderTudo();

    expect(c.leadsFormularios()).toBe(30);
    expect(c.leadsCanais()).toBe(70);
    expect(c.total()).toBe(100);

    // As fatias fecham em 100: é a comparação que justifica a tela existir.
    expect(c.fatiaFormularios()).toBe(30);
    expect(c.fatiaCanais()).toBe(70);

    const t = texto();
    expect(t).toContain('leads captados');
    expect(t).toContain('por formulário do site');
    expect(t).toContain('por QR Code ou link');
  });

  it('sem lead nenhum o resumo mostra zero, e não NaN', () => {
    // Divisão por zero na fatia é o defeito clássico de tela de percentual — e a tela nasce
    // vazia em toda conta nova, que é justamente quando alguém a vê pela primeira vez.
    montar();
    for (let volta = 0; volta < 4; volta++) {
      const pendentes = http.match(() => true);
      if (pendentes.length === 0) break;
      pendentes.forEach(r => r.flush(r.request.url.includes('/canais')
        ? { itens: [], conexoes: [], podeCriar: false, leadsAtribuidos: 0 }
        : []));
    }
    fixture.detectChanges();

    expect(c.total()).toBe(0);
    expect(c.fatiaFormularios()).toBe(0);
    expect(c.fatiaCanais()).toBe(0);
    expect(texto()).not.toContain('NaN');
  });

  it('o resumo diz que o número do QR é um PISO', () => {
    // A honestidade do INT-2 não pode se perder na mudança de tela: não existe denominador para
    // "quantos escanearam e apagaram o código", e um total que finge ser total é pior que a
    // ausência dele.
    montar();
    responderTudo();

    expect(texto()).toContain('piso');
  });

  // ==================================================================== abas
  it('abre em Formulários e só monta o painel da aba ativa', () => {
    montar();
    responderTudo();

    expect(c.aba()).toBe('formularios');

    const raiz = fixture.nativeElement as HTMLElement;
    expect(raiz.querySelector('app-formularios')).not.toBeNull();
    expect(raiz.querySelector('app-canais'))
      .withContext('a aba fechada não pode ficar montada carregando dados').toBeNull();
  });

  it('A ABA VEM DA URL — é o que faz o link antigo de QR cair no lugar certo', () => {
    montar('qr');
    responderTudo();

    expect(c.aba()).toBe('qr');

    const raiz = fixture.nativeElement as HTMLElement;
    expect(raiz.querySelector('app-canais')).not.toBeNull();
    expect(raiz.querySelector('app-formularios')).toBeNull();
  });

  it('parâmetro de aba desconhecido cai na primeira, sem quebrar', () => {
    montar('inexistente');
    responderTudo();
    expect(c.aba()).toBe('formularios');
  });

  it('trocar de aba troca o painel montado', () => {
    montar();
    responderTudo();

    c.trocarAba('qr');
    fixture.detectChanges();
    responderTudo();

    const raiz = fixture.nativeElement as HTMLElement;
    expect(raiz.querySelector('app-canais')).not.toBeNull();
    expect(raiz.querySelector('app-formularios')).toBeNull();
  });

  // ==================================================================== resiliência
  it('UMA LISTA QUE FALHA NÃO APAGA O RESUMO DA OUTRA', () => {
    // Erro num dos dois GET não pode zerar a tela inteira: o dono continua precisando ver o
    // número que ainda dá para calcular.
    montar();

    http.match(r => r.url.includes('/canais'))
      .forEach(r => r.error(new ProgressEvent('erro'), { status: 500 }));
    http.match(r => r.url.includes('/formularios'))
      .forEach(r => r.flush([FORMULARIO]));
    fixture.detectChanges();

    expect(c.leadsFormularios()).toBe(30);
    expect(c.leadsCanais()).toBe(0);
    expect(c.carregandoResumo()).toBeFalse();
  });

  // ==================================================================== largura
  it('é tela DENSA: usa `.pagina` sem o modificador de formulário', () => {
    // Captação tem tabela, número por item e ações por linha — mesma natureza de /equipe.
    // `.pagina.formulario` estreitaria os campos e o cartão, e a tabela sairia espremida.
    montar();
    responderTudo();

    const pagina = (fixture.nativeElement as HTMLElement).querySelector('.pagina')!;
    expect(pagina).not.toBeNull();
    expect(pagina.classList.contains('formulario')).toBeFalse();
  });
});
