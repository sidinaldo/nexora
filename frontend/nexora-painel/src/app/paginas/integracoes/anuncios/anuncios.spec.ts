import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ConversaoDto, CredencialDto } from '../../../nucleo/modelos';
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
    ativo: true,
    emLead: true,
    emCompra: true,
    consentimentoEm: '2026-03-01T12:00:00Z',
    consentimentoPor: 'Ana Dona',
    desativadaEm: null,
    desativadaMotivo: null,
    criadoEm: '2026-03-01T12:00:00Z',
    enviando: true
  };

  let fixture: ComponentFixture<IntegracaoAnuncios>;
  let c: IntegracaoAnuncios;
  let http: HttpTestingController;

  function montar(corpo: {
    credencial: CredencialDto | null;
    leadsComAnuncio30Dias: number;
    conversoes?: ConversaoDto[];
  }) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(IntegracaoAnuncios);
    c = fixture.componentInstance;
    fixture.detectChanges();

    http.expectOne(r => r.url.includes('/conversoes'))
      .flush({ ...corpo, conversoes: corpo.conversoes ?? [] });
    fixture.detectChanges();
  }

  // ==================================================================== o token
  it('O CAMPO DE TOKEN NASCE VAZIO, mesmo com token salvo — e o sufixo aparece no lugar', () => {
    montar({ credencial: CREDENCIAL, leadsComAnuncio30Dias: 0 });

    expect(c.fToken()).toBe('');

    const campo = (fixture.nativeElement as HTMLElement)
      .querySelector('#token') as HTMLInputElement;

    // É a única pista de QUAL token está guardado. Sem ela, quem abre a tela para trocar o Pixel
    // ID vê o campo vazio e conclui que o token se perdeu.
    expect(campo.placeholder).toContain('EAAG…4Zc');
    expect(campo.type).toBe('password');
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
        leadsComAnuncio30Dias: 0, conversoes: []
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

  /** O GET que o `testar()` dispara em seguida. */
  function responderRecarga(credencial: CredencialDto | null) {
    http.expectOne(r => r.method === 'GET' && r.url.includes('/conversoes'))
      .flush({ credencial, leadsComAnuncio30Dias: 0, conversoes: [] });
  }
});
