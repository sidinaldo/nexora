import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { LOCALE_ID, provideZonelessChangeDetection } from '@angular/core';
import { registerLocaleData } from '@angular/common';
import ptBr from '@angular/common/locales/pt';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import {
  ConversaoDto, CredencialDto, VendaSemConversaoDto, VendasSemEnvio
} from '../../../nucleo/modelos';
import { IntegracaoAnuncios } from './anuncios';

/** ANÚNCIOS — o painel que conecta o pixel da Meta (INT-4).
 *
 *  ===================== O QUE ESTE ARQUIVO PROTEGE =====================
 *  Duas coisas que quebram em silêncio:
 *
 *  1. **O token nunca volta da API**, então o campo nasce vazio e vazio significa "mantém o que
 *     está lá". Se um dia a tela passar a mandar string vazia, salvar qualquer outro campo APAGA o
 *     token do cliente — e o sintoma é "paramos de receber conversão", semanas depois.
 *
 *  2. **O número que cobra** só aparece quando há número e quando NÃO está enviando. Mostrá-lo
 *     para quem já conectou seria dizer que está perdendo lead quando não está.
 *  ====================================================================== */
describe('integrações — anúncios', () => {
  const CREDENCIAL: CredencialDto = {
    id: 1,
    plataforma: 'meta',
    identificador: '1234567890123456',
    tokenFinal: 'EAAG…4Zc',
    codigoTeste: null,
    paginaId: null,
    ativo: true,
    emLead: true,
    emCompra: true,
    consentimentoEm: '2026-03-01T12:00:00Z',
    consentimentoPor: 'Ana Dona',
    desativadaEm: null,
    desativadaMotivo: null,
    criadoEm: '2026-03-01T12:00:00Z',
    enviando: true,
    motivosParados: []
  };

  /** A credencial do caso real: tudo preenchido, consentimento NÃO marcado. É assim que seis
   *  vendas se perderam com a tela mostrando a integração configurada. */
  const SEM_CONSENTIMENTO: CredencialDto = {
    ...CREDENCIAL,
    consentimentoEm: null,
    consentimentoPor: null,
    enviando: false,
    motivosParados: ['Falta marcar o consentimento — é ele que autoriza o envio.']
  };

  const VENDA_NO_PRAZO: VendaSemConversaoDto = {
    negociacaoId: 55,
    contato: 'Ysianne',
    valor: 556.12,
    ganhaEm: '2026-03-19T12:00:00Z',
    expiraEm: '2026-03-26T12:00:00Z',
    foraDoPrazo: false
  };

  const VENDA_VENCIDA: VendaSemConversaoDto = {
    negociacaoId: 56,
    contato: 'Antiga',
    valor: 100,
    ganhaEm: '2026-03-01T12:00:00Z',
    expiraEm: '2026-03-08T12:00:00Z',
    foraDoPrazo: true
  };

  const SEM_ENVIO_VAZIO: VendasSemEnvio = {
    total: 0, valorTotal: 0, noPrazo: 0, diasDaJanela: 21, vendas: []
  };

  let fixture: ComponentFixture<IntegracaoAnuncios>;
  let c: IntegracaoAnuncios;
  let http: HttpTestingController;

  function montar(corpo: {
    credencial: CredencialDto | null;
    leadsComAnuncio30Dias: number;
    conversoes?: ConversaoDto[];
    vendasSemEnvio?: VendasSemEnvio;
  }) {
    // ⚠️ A LOCALE ENTRA AQUI PORQUE A PRODUÇÃO A TEM (`app.config.ts`). Sem ela o TestBed roda em
    // en-US e `currency:'BRL'` sai "R$556.12" — o teste afirmaria uma formatação que nenhum usuário
    // vê, e passaria enquanto a tela mostrasse outra coisa.
    registerLocaleData(ptBr);

    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: LOCALE_ID, useValue: 'pt-BR' }
      ]
    });

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(IntegracaoAnuncios);
    c = fixture.componentInstance;
    fixture.detectChanges();

    http.expectOne(r => r.url.includes('/conversoes'))
      .flush({
        ...corpo,
        conversoes: corpo.conversoes ?? [],
        // Vazio por omissão: o caso comum é não haver venda perdida, e é o que a maioria dos
        // testes daqui quer.
        vendasSemEnvio: corpo.vendasSemEnvio ?? SEM_ENVIO_VAZIO
      });
    fixture.detectChanges();
  }

  // ==================================================================== o token
  it('O CAMPO DE TOKEN NASCE VAZIO, e o sufixo guardado aparece FORA dele, num selo', () => {
    montar({ credencial: CREDENCIAL, leadsComAnuncio30Dias: 0 });

    expect(c.fToken()).toBe('');

    const campo = (fixture.nativeElement as HTMLElement)
      .querySelector('#token') as HTMLInputElement;
    expect(campo.type).toBe('password');

    // ⚠️ O SUFIXO NÃO PODE ESTAR NO PLACEHOLDER. Placeholder é cinza e mora dentro do campo:
    // o olho lê campo vazio, e quem abre a tela para trocar o Pixel ID conclui que o token se
    // perdeu. Foi o que aconteceu de verdade na primeira vez que o dono olhou esta tela.
    expect(campo.placeholder).not.toContain('EAAG…4Zc');
    expect(campo.placeholder).toContain('substituir');

    const selo = (fixture.nativeElement as HTMLElement)
      .querySelector('.rotulo-com-selo .selo') as HTMLElement;
    expect(selo).toBeTruthy();
    expect(selo.textContent).toContain('EAAG…4Zc');
    expect(selo.textContent).toContain('guardado');
    expect(selo.classList).toContain('selo-ok');
  });

  it('COM O TOKEN RECUSADO, o selo diz recusado — e não fica verde contradizendo o erro', () => {
    // Guardado e morto são coisas diferentes, e o selo é o que fica colado no campo. Um "guardado"
    // verde logo abaixo da caixa que diz "a Meta recusou o token" manda a pessoa para o lado errado.
    montar({
      credencial: {
        ...CREDENCIAL,
        enviando: false,
        desativadaEm: '2026-03-10T12:00:00Z',
        desativadaMotivo: 'A Meta recusou o token. Gere um novo em Gerenciador de Eventos.'
      },
      leadsComAnuncio30Dias: 0
    });

    const selo = (fixture.nativeElement as HTMLElement)
      .querySelector('.rotulo-com-selo .selo') as HTMLElement;
    expect(selo.textContent).toContain('recusado pela Meta');
    expect(selo.textContent).toContain('EAAG…4Zc');
    expect(selo.classList).toContain('selo-perigo');
    expect(selo.classList).not.toContain('selo-ok');
  });

  it('SALVAR COM O CAMPO VAZIO MANDA `token: null` — nunca string vazia', () => {
    // ⚠️ O TESTE QUE IMPEDE O PIOR DEFEITO DESTA TELA. String vazia é um token, tecnicamente:
    // o servidor a gravaria, e o envio passaria a falhar com 190 sem ninguém ter mexido no token.
    montar({ credencial: CREDENCIAL, leadsComAnuncio30Dias: 0 });

    c.fPixel.set('9999999999');
    c.salvar();

    const put = http.expectOne(r => r.method === 'PUT');
    expect(put.request.body.token).toBeNull();
    expect(put.request.body.identificador).toBe('9999999999');
  });

  it('com token digitado, ele VAI no corpo', () => {
    montar({ credencial: CREDENCIAL, leadsComAnuncio30Dias: 0 });

    c.fToken.set('  EAAGnovo123  ');
    c.salvar();

    expect(http.expectOne(r => r.method === 'PUT').request.body.token).toBe('EAAGnovo123');
  });

  // ==================================================================== a página (Clique-para-WhatsApp)
  it('O ID DA PAGINA VAI NO CORPO, e vazio vira null', () => {
    // ⚠️ O CAMPO NASCEU DE UMA RECUSA DA META no primeiro envio real: sem `page_id`, ela rejeita o
    // evento de Clique-para-WhatsApp inteiro (`error_subcode 2804116`). Nulo é o caso normal — e aí
    // o lead do WhatsApp sai como `chat`, que funciona.
    montar({ credencial: CREDENCIAL, leadsComAnuncio30Dias: 0 });

    c.salvar();
    expect(http.expectOne(r => r.method === 'PUT').request.body.paginaId).toBeNull();
  });

  it('com a página preenchida, ela viaja sem espaço em volta', () => {
    montar({ credencial: CREDENCIAL, leadsComAnuncio30Dias: 0 });

    c.fPaginaId.set('  778899001122  ');
    c.salvar();

    expect(http.expectOne(r => r.method === 'PUT').request.body.paginaId).toBe('778899001122');
  });

  // ==================================================================== o número que cobra
  it('O NÚMERO QUE COBRA aparece para quem NÃO está enviando', () => {
    montar({ credencial: null, leadsComAnuncio30Dias: 12 });

    expect(c.perdendo()).toBeTrue();
    const texto = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(texto).toContain('12');
    expect(texto).toContain('não ficou sabendo');
  });

  it('e NÃO aparece para quem já conectou', () => {
    // Dizer "você está perdendo 12 leads" para quem conectou seria mentira — e a próxima frase da
    // tela perderia crédito junto.
    montar({ credencial: CREDENCIAL, leadsComAnuncio30Dias: 12 });

    expect(c.perdendo()).toBeFalse();
    expect((fixture.nativeElement as HTMLElement).querySelector('.perdendo')).toBeNull();
  });

  it('nem quando ninguém veio de anúncio', () => {
    // Sem rastro chegando, a mensagem seria conselho genérico — e conselho genérico numa tela de
    // configuração é ruído.
    montar({ credencial: null, leadsComAnuncio30Dias: 0 });

    expect(c.perdendo()).toBeFalse();
  });

  // ==================================================================== consentimento
  it('O CONSENTIMENTO MOSTRA QUEM DECLAROU E QUANDO', () => {
    // Declaração sem data e sem autor não responde a um pedido da ANPD — é por isso que ela não é
    // um `bool`, e é por isso que a tela mostra os dois.
    montar({ credencial: CREDENCIAL, leadsComAnuncio30Dias: 0 });

    expect(c.fConsentimento()).toBeTrue();
    const texto = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(texto).toContain('01/03/2026');
    expect(texto).toContain('Ana Dona');
  });

  it('sem consentimento, a caixinha nasce DESMARCADA', () => {
    montar({ credencial: { ...CREDENCIAL, consentimentoEm: null, consentimentoPor: null, enviando: false },
             leadsComAnuncio30Dias: 0 });

    expect(c.fConsentimento()).toBeFalse();
  });

  // ==================================================================== o token que morreu
  it('QUANDO A META RECUSA O TOKEN, a tela diz O QUE FAZER', () => {
    // Sem esta frase os eventos param em silêncio: a credencial fica lá, marcada como ativa pela
    // pessoa, e nada sai. O motivo vem do servidor já em português.
    montar({
      credencial: {
        ...CREDENCIAL,
        enviando: false,
        desativadaEm: '2026-03-20T10:00:00Z',
        desativadaMotivo: 'A Meta recusou o token. Gere um novo em Gerenciador de Eventos.'
      },
      leadsComAnuncio30Dias: 3
    });

    const texto = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(texto).toContain('A Meta recusou o token');
    expect(texto).toContain('Cole o token novo abaixo');

    // E o selo diz "parado", não "enviando".
    expect(texto).toContain('parado');
  });

  it('erro do servidor aparece no formulário, e não como tela em branco', () => {
    montar({ credencial: null, leadsComAnuncio30Dias: 0 });

    c.fPixel.set('nao-e-numero');
    c.salvar();

    http.expectOne(r => r.method === 'PUT').flush(
      { erro: 'O ID do pixel é só números.' }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(c.erroForm()).toContain('só números');
    expect(c.salvando()).toBeFalse();
  });
  // ==================================================================== o botão de teste
  const CONVERSAO: ConversaoDto = {
    id: 7, tipo: 'compra', status: 'entregue', contato: 'Bruna Lima', valor: 1450.5,
    tentativas: 1, codigoResposta: 200, codigoMeta: null, fbtraceId: 'AbC1',
    erro: null, ocorridoEm: '2026-03-20T12:00:00Z', expiraEm: '2026-03-27T12:00:00Z',
    entregueEm: '2026-03-20T12:01:00Z', criadoEm: '2026-03-20T12:00:00Z',
    payload: '{"data":[{"event_name":"Purchase"}]}', podeReenviar: false
  };

  it('SEM TOKEN GUARDADO NÃO HÁ BOTÃO DE TESTE', () => {
    // Antes disso ele só pode falhar — e um botão que só falha ensina a pessoa a não confiar na
    // tela.
    montar({ credencial: null, leadsComAnuncio30Dias: 0 });
    expect(textoDaTela()).not.toContain('Enviar evento de teste');
  });

  it('com token guardado, o botão de teste aparece', () => {
    montar({ credencial: CREDENCIAL, leadsComAnuncio30Dias: 0 });
    expect(textoDaTela()).toContain('Enviar evento de teste');
  });

  it('O TESTE QUE PASSA SEM CÓDIGO DE TESTE AVISA QUE ENTROU COMO LEAD REAL', () => {
    // ⚠️ A FRASE QUE EVITA UM ESTRAGO SILENCIOSO. Sem `test_event_code`, o evento de teste entra na
    // otimização das campanhas do cliente como um lead de verdade — e ele nunca saberia, porque a
    // resposta da Meta é a mesma "aceitei".
    montar({ credencial: { ...CREDENCIAL, codigoTeste: null }, leadsComAnuncio30Dias: 0 });

    c.testar();
    http.expectOne(r => r.url.endsWith('/testar'))
      .flush({ ok: true, codigo: 200, fbtraceId: 'T1', erro: null });
    // ⚠️ O teste RECARREGA o painel (ele pode ter desligado ou religado a credencial), então sem
    // responder este GET a tela fica em "Carregando…" e nada do resultado aparece.
    responderRecarga({ ...CREDENCIAL, codigoTeste: null });
    fixture.detectChanges();

    const texto = textoDaTela();
    expect(texto).toContain('A Meta aceitou');
    expect(texto).toContain('entrou');
    expect(texto).toContain('código de teste');
  });

  it('com código de teste, a frase manda olhar "Eventos de teste"', () => {
    montar({ credencial: { ...CREDENCIAL, codigoTeste: 'TEST123' }, leadsComAnuncio30Dias: 0 });

    c.testar();
    http.expectOne(r => r.url.endsWith('/testar'))
      .flush({ ok: true, codigo: 200, fbtraceId: 'T1', erro: null });
    responderRecarga({ ...CREDENCIAL, codigoTeste: 'TEST123' });
    fixture.detectChanges();

    const texto = textoDaTela();
    expect(texto).toContain('Eventos de teste');
    expect(texto).toContain('não entra na otimização');
  });

  it('O TESTE QUE FALHA MOSTRA O ERRO E O fbtrace_id', () => {
    // O `fbtrace_id` é a primeira coisa que o suporte da Meta pede. Sem ele na tela, o chamado do
    // cliente começa com "não temos como rastrear".
    montar({ credencial: CREDENCIAL, leadsComAnuncio30Dias: 0 });

    c.testar();
    http.expectOne(r => r.url.endsWith('/testar')).flush({
      ok: false, codigo: 200, fbtraceId: 'XyZ789',
      erro: 'A Meta recusou: Invalid OAuth access token.'
    });
    responderRecarga(CREDENCIAL);
    fixture.detectChanges();

    const texto = textoDaTela();
    expect(texto).toContain('A Meta recusou');
    expect(texto).toContain('XyZ789');
  });

  it('o teste RECARREGA a credencial — porque ele pode ter desligado ou religado o envio', () => {
    montar({ credencial: CREDENCIAL, leadsComAnuncio30Dias: 0 });

    c.testar();
    http.expectOne(r => r.url.endsWith('/testar'))
      .flush({ ok: false, codigo: 200, fbtraceId: null, erro: 'token ruim' });

    // O GET de novo: sem isto, o selo continuaria dizendo "enviando" depois de o servidor ter
    // desativado a credencial.
    http.expectOne(r => r.method === 'GET' && r.url.includes('/conversoes'))
      .flush({
        credencial: { ...CREDENCIAL, enviando: false, desativadaMotivo: 'A Meta recusou o token.' },
        leadsComAnuncio30Dias: 0, conversoes: [], vendasSemEnvio: SEM_ENVIO_VAZIO
      });
    fixture.detectChanges();

    expect(textoDaTela()).toContain('parado');
  });

  // ==================================================================== o registro
  it('A TABELA MOSTRA O NOME DO EVENTO NA LÍNGUA DA META', () => {
    // É o nome que o cliente vê no Gerenciador de Eventos dele. Usar "compra" aqui faria a nossa
    // tela e a dele não conversarem.
    montar({ credencial: CREDENCIAL, leadsComAnuncio30Dias: 0, conversoes: [CONVERSAO] });

    const texto = textoDaTela();
    expect(texto).toContain('Purchase');
    expect(texto).toContain('Bruna Lima');
    expect(texto).toContain('enviado');
  });

  it('O EXPIRADO NÃO GANHA BOTÃO DE REENVIO', () => {
    // ⚠️ É A RAZÃO DE `expirado` SER UM STATUS PRÓPRIO. Como `falhou`, a tela ofereceria um gesto
    // que só pode fracassar — e o dono clicaria, veria falhar, e clicaria de novo. Quem decide é o
    // SERVIDOR, em `podeReenviar`.
    montar({
      credencial: CREDENCIAL, leadsComAnuncio30Dias: 0,
      conversoes: [{ ...CONVERSAO, status: 'expirado', podeReenviar: false, entregueEm: null }]
    });

    expect(textoDaTela()).toContain('fora do prazo');
    expect(textoDaTela()).not.toContain('Reenviar');
  });

  it('O QUE FALHOU GANHA O BOTÃO, e ele chama a rota de reenvio', () => {
    montar({
      credencial: CREDENCIAL, leadsComAnuncio30Dias: 0,
      conversoes: [{
        ...CONVERSAO, status: 'falhou', podeReenviar: true, entregueEm: null,
        erro: 'A Meta recusou o token.'
      }]
    });

    expect(textoDaTela()).toContain('Reenviar');
    // O erro aparece na linha: sem ele, "falhou" não diz o que fazer.
    expect(textoDaTela()).toContain('A Meta recusou o token.');

    c.reenviar({ ...CONVERSAO, id: 7 });
    expect(http.expectOne(r => r.url.endsWith('/conversoes/7/reenviar')).request.method).toBe('POST');
  });

  it('O PAYLOAD ABRE E FECHA, e não tem token dentro', () => {
    // O corpo aparece na tela porque "não está chegando na Meta" termina sempre em "o que
    // exatamente vocês mandaram?". E o token nunca fez parte dele.
    montar({ credencial: CREDENCIAL, leadsComAnuncio30Dias: 0, conversoes: [CONVERSAO] });

    expect(textoDaTela()).not.toContain('event_name');

    c.alternarPayload(7);
    fixture.detectChanges();

    const texto = textoDaTela();
    expect(texto).toContain('event_name');
    expect(texto).toContain('criptografados');
    expect(texto).not.toContain('access_token');

    c.alternarPayload(7);
    fixture.detectChanges();
    expect(textoDaTela()).not.toContain('event_name');
  });

  it('a contagem de falhas aparece sem ninguém precisar procurar', () => {
    montar({
      credencial: CREDENCIAL, leadsComAnuncio30Dias: 0,
      conversoes: [
        { ...CONVERSAO, id: 1, status: 'falhou', podeReenviar: true },
        { ...CONVERSAO, id: 2, status: 'entregue' },
        { ...CONVERSAO, id: 3, status: 'falhou', podeReenviar: true }
      ]
    });

    expect(c.falhas()).toBe(2);
    expect(textoDaTela()).toContain('falharam');
  });

  function textoDaTela(): string {
    return (fixture.nativeElement as HTMLElement).textContent ?? '';
  }

  // ==================================================================== INT-5 · vendas perdidas

  /** O painel com vendas perdidas e o envio LIGADO. */
  function montarComVendas(vendas: VendaSemConversaoDto[], credencial = CREDENCIAL) {
    montar({
      credencial,
      leadsComAnuncio30Dias: 0,
      vendasSemEnvio: {
        total: vendas.length,
        valorTotal: vendas.reduce((soma, v) => soma + (v.valor ?? 0), 0),
        noPrazo: vendas.filter(v => !v.foraDoPrazo).length,
        diasDaJanela: 21,
        vendas
      }
    });
  }

  it('O AVISO DIZ QUAL CONDIÇÃO FALTA, e não só "parado"', () => {
    // ===================== O DEFEITO QUE ORIGINOU O BLOCO =====================
    // O selo dizia `parado` e pronto. São cinco condições, e o dono não tinha como saber qual era
    // a dele — então não consertava, e seis vendas se perderam com a tela mostrando a integração
    // configurada.
    // =========================================================================
    montar({ credencial: SEM_CONSENTIMENTO, leadsComAnuncio30Dias: 0 });

    const tela = textoDaTela();
    expect(tela).toContain('nada está sendo enviado');
    expect(tela).toContain('Falta marcar o consentimento');
  });

  it('COM DUAS CONDIÇÕES FALTANDO, a tela mostra AS DUAS', () => {
    // "Conserte isto" seguido de "agora conserte aquilo" é o jeito mais rápido de alguém desistir
    // no meio — e quem acabou de conectar costuma ter duas pendências, não uma.
    montar({
      credencial: {
        ...SEM_CONSENTIMENTO,
        motivosParados: ['Falta marcar o consentimento.', 'Falta o token da API de Conversões.']
      },
      leadsComAnuncio30Dias: 0
    });

    const tela = textoDaTela();
    expect(tela).toContain('consentimento');
    expect(tela).toContain('token da API');
  });

  it('SEM MOTIVO NENHUM, o aviso não aparece', () => {
    montar({ credencial: CREDENCIAL, leadsComAnuncio30Dias: 0 });

    expect(textoDaTela()).not.toContain('nada está sendo enviado');
  });

  it('A LISTA SÓ APARECE QUANDO HÁ VENDA PERDIDA', () => {
    // ⚠️ Um painel que diz "0 pendências" todo dia é ruído — e ruído é o que fez ninguém reparar
    // no selo `parado`. Se o bloco aparece, é porque há o que fazer.
    montar({ credencial: CREDENCIAL, leadsComAnuncio30Dias: 0 });

    expect(textoDaTela()).not.toContain('Vendas que não chegaram');
  });

  it('A LISTA MOSTRA O QUE O DONO PRECISA PARA RECONHECER A VENDA, e o total em dinheiro', () => {
    // "6 vendas pendentes" se lê como aviso técnico; o valor se lê como prejuízo.
    montarComVendas([VENDA_NO_PRAZO]);

    const tela = textoDaTela();
    expect(tela).toContain('Ysianne');
    expect(tela).toContain('556,12');
    expect(tela).toContain('Vendas que não chegaram');
  });

  it('A VENDA FORA DO PRAZO NÃO GANHA BOTÃO DE ENVIAR', () => {
    // ⚠️ `foraDoPrazo` vem do SERVIDOR, com o relógio dele. A tela não recalcula a janela: oferecer
    // um botão que o servidor vai recusar é oferecer um gesto que só pode fracassar.
    montarComVendas([VENDA_VENCIDA]);

    expect(textoDaTela()).toContain('fora do prazo');
    expect(botoesDaLista()).toEqual([]);
  });

  it('O BOTÃO DO LOTE DIZ QUANTAS DÃO TEMPO, e não "todas"', () => {
    // ⚠️ "Enviar todas" acima de linhas que NÃO podem ser enviadas é mentira — e o primeiro
    // chamado de suporte.
    montarComVendas([VENDA_NO_PRAZO, VENDA_VENCIDA]);

    expect(textoDaTela()).toContain('Enviar as 1 que ainda dão tempo');
    expect(textoDaTela()).not.toContain('Enviar todas');
  });

  /** ⚠️ O NÚMERO DO BOTÃO É O DO SERVIDOR (AUD-XX). A lista tem teto de 50; aqui vêm só duas linhas
   *  e o servidor diz 52 no prazo. Contar a lista diria 1. */
  it('O BOTÃO DO LOTE USA O "NO PRAZO" DO SERVIDOR, e não a contagem da lista', () => {
    montar({
      credencial: CREDENCIAL,
      leadsComAnuncio30Dias: 0,
      vendasSemEnvio: {
        total: 60, valorTotal: 600, noPrazo: 52, diasDaJanela: 21,
        vendas: [VENDA_NO_PRAZO, VENDA_VENCIDA]
      }
    });

    expect(textoDaTela()).toContain('Enviar as 52 que ainda dão tempo');
  });

  it('COM O ENVIO PARADO, NENHUMA LINHA GANHA BOTÃO — e a tela manda resolver o aviso', () => {
    // Clicar não funcionaria: o servidor recusa. Em vez de um botão morto, a linha aponta para o
    // aviso de cima.
    montarComVendas([VENDA_NO_PRAZO], SEM_CONSENTIMENTO);

    expect(botoesDaLista()).toEqual([]);
    expect(textoDaTela()).toContain('Resolva o aviso acima');
  });

  it('ENVIAR uma venda chama a rota DELA e recarrega', () => {
    montarComVendas([VENDA_NO_PRAZO]);

    c.enviarVenda(VENDA_NO_PRAZO);

    // ⚠️ A ROTA É `vendas/{id}/enviar`, e não `{id}/reenviar`: o id aqui é de uma NEGOCIAÇÃO, e
    // o do reenvio é de um EVENTO. Dois espaços de id na mesma posição é como as duas
    // funcionalidades viram uma só por engano.
    http.expectOne(r => r.method === 'POST'
                     && r.url.endsWith('/conversoes/vendas/55/enviar')).flush(null);

    http.expectOne(r => r.method === 'GET' && r.url.includes('/conversoes')).flush({
      credencial: CREDENCIAL, leadsComAnuncio30Dias: 0, conversoes: [],
      vendasSemEnvio: SEM_ENVIO_VAZIO
    });
    fixture.detectChanges();

    expect(textoDaTela()).not.toContain('Vendas que não chegaram');
  });

  it('ENVIAR O LOTE manda UMA requisição só', () => {
    montarComVendas([VENDA_NO_PRAZO, VENDA_VENCIDA]);

    c.enviarPendentes();

    http.expectOne(r => r.method === 'POST'
                     && r.url.endsWith('/conversoes/vendas/enviar-pendentes'))
      .flush({ enfileiradas: 1, restantes: 0 });

    http.expectOne(r => r.method === 'GET' && r.url.includes('/conversoes')).flush({
      credencial: CREDENCIAL, leadsComAnuncio30Dias: 0, conversoes: [],
      vendasSemEnvio: SEM_ENVIO_VAZIO
    });
  });

  it('ENVIAR NÃO PISCA A TELA — o conteúdo nunca sai do ar', () => {
    /** ===================== O CLIQUE NÃO PODE DEVOLVER O DONO AO TOPO =====================
     *  `carregando` embrulha a PÁGINA INTEIRA. Ligá-lo troca todo o conteúdo por um
     *  "carregando…", o documento encolhe, e o navegador joga o scroll para o começo.
     *
     *  ⚠️ RELATADO NA TELA, com a feature recém-publicada: "a cada clique no envio a tela dá
     *  refresh e vai para o top da página". Com seis vendas para enviar, são seis saltos — e a
     *  pessoa perde o lugar toda vez.
     *
     *  O teste afirma o SINAL e o DOM: só o sinal deixaria passar uma versão que o liga e desliga
     *  rápido demais para o `detectChanges` ver, e só o DOM não diria por quê.
     *  ================================================================================= */
    montarComVendas([VENDA_NO_PRAZO]);

    c.enviarVenda(VENDA_NO_PRAZO);
    http.expectOne(r => r.method === 'POST').flush(null);

    expect(c.carregando())
      .withContext('a página não entra em estado de carregamento').toBeFalse();

    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.nao-enviadas'))
      .withContext('e o conteúdo continua no ar enquanto o GET volta').not.toBeNull();

    http.expectOne(r => r.method === 'GET' && r.url.includes('/conversoes')).flush({
      credencial: CREDENCIAL, leadsComAnuncio30Dias: 0, conversoes: [],
      vendasSemEnvio: SEM_ENVIO_VAZIO
    });
  });

  it('A RECARGA SILENCIOSA NÃO APAGA O QUE A PESSOA ESTÁ DIGITANDO', () => {
    // O `preencher` zera o campo de token a cada carregamento — é correto no primeiro, e destrutivo
    // depois de uma ação: quem estivesse colando um token perderia o que digitou ao enviar.
    montarComVendas([VENDA_NO_PRAZO]);

    c.fToken.set('EAAG-token-sendo-digitado');
    c.enviarVenda(VENDA_NO_PRAZO);
    http.expectOne(r => r.method === 'POST').flush(null);

    http.expectOne(r => r.method === 'GET' && r.url.includes('/conversoes')).flush({
      credencial: CREDENCIAL, leadsComAnuncio30Dias: 0, conversoes: [],
      vendasSemEnvio: SEM_ENVIO_VAZIO
    });

    expect(c.fToken()).toBe('EAAG-token-sendo-digitado');
  });

  it('O RODAPÉ DIZ ATÉ QUANDO A LISTA SABE', () => {
    // Sem isto, a ausência de uma venda antiga parece defeito.
    montarComVendas([VENDA_NO_PRAZO]);

    expect(textoDaTela()).toContain('21 dias');
    expect(textoDaTela()).toContain('antes de você conectar o pixel');
  });

  /** Os botões DENTRO da lista de vendas perdidas — não os da tela toda. */
  function botoesDaLista(): string[] {
    const bloco = fixture.nativeElement.querySelector('.nao-enviadas') as HTMLElement | null;
    if (!bloco) return [];
    return [...bloco.querySelectorAll('tbody button')].map(b => b.textContent!.trim());
  }

  /** O GET que o `testar()` dispara em seguida. */
  function responderRecarga(credencial: CredencialDto | null) {
    http.expectOne(r => r.method === 'GET' && r.url.includes('/conversoes'))
      .flush({
        credencial, leadsComAnuncio30Dias: 0, conversoes: [],
        vendasSemEnvio: SEM_ENVIO_VAZIO
      });
  }
});
