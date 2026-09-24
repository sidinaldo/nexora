import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { CredencialDto } from '../../../nucleo/modelos';
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

  function montar(corpo: { credencial: CredencialDto | null; leadsComAnuncio30Dias: number }) {
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

    http.expectOne(r => r.url.includes('/conversoes')).flush(corpo);
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
});
