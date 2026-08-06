import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { EntregaWebhookDto, PainelWebhook, WebhookDto } from '../../nucleo/modelos';
import { Integracoes } from './integracoes';

/** INTEGRAÇÕES — o webhook de saída na tela (INT-3).
 *
 *  ===================== O QUE ESTE ARQUIVO PROTEGE =====================
 *  Quatro coisas que somem numa refatoração e não voltam por conta própria:
 *
 *    1. o SEGREDO aparece uma vez e não é buscado de novo — se a tela passar a lê-lo do GET,
 *       ele volta a viver no histórico do navegador e no cache do proxy;
 *    2. o aviso de LGPD fica colado na opção que o resolve. Aviso longe do controle é aviso que
 *       ninguém liga à decisão que está tomando;
 *    3. o exemplo de validação assina `timestamp.corpo` e compara em tempo constante — um
 *       snippet errado na tela vira receptor inseguro em todo cliente que copiar;
 *    4. só a entrega que FALHOU oferece reenviar.
 *  ====================================================================== */
describe('integrações — webhook de saída', () => {
  function webhook(over: Partial<WebhookDto> = {}): WebhookDto {
    return {
      id: 1, url: 'https://webhook.cliente.com/nexora', ativo: true, somenteIds: false,
      emLeadCriado: true, emLeadMovido: true, emVendaFechada: true, emVendaPerdida: true,
      emMensagemRecebida: false, criadoEm: '2026-08-01T10:00:00Z',
      ...over
    };
  }

  function entrega(over: Partial<EntregaWebhookDto> = {}): EntregaWebhookDto {
    return {
      id: 1, evento: 'lead.criado', status: 'entregue', tentativas: 1,
      codigoResposta: 200, erro: null, proximaTentativaEm: null,
      entregueEm: '2026-08-06T12:00:01Z', criadoEm: '2026-08-06T12:00:00Z',
      payload: '{"versao":1,"evento":"lead.criado","dados":{"id":42}}',
      podeReenviar: false,
      ...over
    };
  }

  let http: HttpTestingController;
  let fixture: ComponentFixture<Integracoes>;
  let c: Integracoes;

  function montar(resposta: PainelWebhook) {
    fixture = TestBed.createComponent(Integracoes);
    c = fixture.componentInstance;
    fixture.detectChanges();

    http.expectOne(r => r.url.endsWith('/webhooks-saida') && r.method === 'GET').flush(resposta);
    fixture.detectChanges();
  }

  function texto(): string { return (fixture.nativeElement as HTMLElement).textContent ?? ''; }

  function botoes(rotulo: string): HTMLButtonElement[] {
    const alvo = rotulo.toLowerCase();
    return [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')]
      .filter(b => b.textContent?.trim().toLowerCase().startsWith(alvo)) as HTMLButtonElement[];
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => TestBed.resetTestingModule());

  // ==================================================================== o segredo
  it('O SEGREDO APARECE UMA VEZ, NA CRIAÇÃO — E NUNCA VEM DO GET', () => {
    montar({ webhook: null, entregas: [] });

    // O payload de leitura NÃO tem segredo. Se um dia tiver, este teste continua passando — por
    // isso a asserção seguinte olha a TELA, que é onde o vazamento apareceria.
    expect(texto()).not.toContain('Guarde o segredo agora');

    c.fUrl.set('https://webhook.cliente.com/nexora');
    c.salvar();

    const put = http.expectOne(r => r.url.endsWith('/webhooks-saida') && r.method === 'PUT');
    expect(put.request.body.url).toBe('https://webhook.cliente.com/nexora');
    put.flush({ segredo: { id: 1, segredo: 'abc123def456', novo: true } });

    http.expectOne(r => r.url.endsWith('/webhooks-saida') && r.method === 'GET')
      .flush({ webhook: webhook(), entregas: [] });
    fixture.detectChanges();

    expect(texto()).toContain('Guarde o segredo agora');
    expect(texto()).toContain('não aparece de novo');

    const campo = (fixture.nativeElement as HTMLElement)
      .querySelector('.segredo-revelado input') as HTMLInputElement;
    expect(campo.value).toBe('abc123def456');
    expect(campo.readOnly).toBeTrue();
  });

  it('salvar de novo NÃO apaga o segredo já revelado na tela', () => {
    // Quem acabou de criar e clicou em "Salvar" outra vez antes de copiar perderia a chave — e ela
    // não volta por nenhum caminho.
    montar({ webhook: null, entregas: [] });

    c.fUrl.set('https://webhook.cliente.com/nexora');
    c.salvar();
    http.expectOne(r => r.method === 'PUT').flush({ segredo: { id: 1, segredo: 'chave', novo: true } });
    http.expectOne(r => r.method === 'GET').flush({ webhook: webhook(), entregas: [] });

    c.salvar();
    http.expectOne(r => r.method === 'PUT').flush({ segredo: null });   // atualização: sem segredo
    http.expectOne(r => r.method === 'GET').flush({ webhook: webhook(), entregas: [] });
    fixture.detectChanges();

    expect(c.segredo()?.segredo).toBe('chave');
  });

  // ==================================================================== privacidade
  it('O AVISO DE LGPD FICA JUNTO DA OPÇÃO QUE O RESOLVE', () => {
    montar({ webhook: webhook(), entregas: [] });

    const t = texto();
    expect(t).toContain('tratamento de dado pessoal');
    expect(t).toContain('Enviar só os IDs');

    // Aviso e controle no MESMO cartão: separados, a pessoa lê o alerta no rodapé e não liga à
    // caixa que ela acabou de deixar desmarcada.
    const cartao = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.cartao')]
      .find(el => el.querySelector('.aviso-lgpd'));
    expect(cartao).withContext('o aviso de LGPD sumiu da tela').toBeDefined();
    expect(cartao!.textContent).toContain('Enviar só os IDs');
  });

  it('o modo só ids vai no corpo do PUT', () => {
    montar({ webhook: webhook(), entregas: [] });

    c.fSomenteIds.set(true);
    c.alternarEvento('emMensagemRecebida');
    c.salvar();

    const put = http.expectOne(r => r.method === 'PUT');
    expect(put.request.body.somenteIds).toBeTrue();
    expect(put.request.body.emMensagemRecebida).toBeTrue();
    put.flush({ segredo: null });
    http.expectOne(r => r.method === 'GET').flush({ webhook: webhook(), entregas: [] });
  });

  it('mensagem.recebida vem desmarcado e avisa que é o de maior volume', () => {
    montar({ webhook: webhook(), entregas: [] });

    expect(c.marcado('emMensagemRecebida')).toBeFalse();
    expect(texto()).toContain('MAIOR volume');
  });

  // ==================================================================== teste
  it('O BOTÃO DE TESTE MOSTRA O RESULTADO NA TELA', () => {
    // Ele resolve a maior parte dos chamados sozinho — mas só se disser o que aconteceu.
    montar({ webhook: webhook(), entregas: [] });

    c.testar();
    http.expectOne(r => r.url.endsWith('/webhooks-saida/testar') && r.method === 'POST')
      .flush({ ok: false, codigo: 404, erro: 'O receptor respondeu 404 Not Found.' });
    http.expectOne(r => r.method === 'GET').flush({ webhook: webhook(), entregas: [] });
    fixture.detectChanges();

    const t = texto();
    expect(t).toContain('Não funcionou');
    expect(t).toContain('404');
    // E diz que nada real foi enviado — senão o dono fica sem saber se criou lixo no sistema dele.
    expect(t).toContain('nenhum dado real foi enviado');
  });

  it('sem webhook configurado não há botão de teste', () => {
    montar({ webhook: null, entregas: [] });
    expect(botoes('Enviar evento de teste').length).toBe(0);
  });

  // ==================================================================== entregas
  it('SÓ A ENTREGA QUE FALHOU OFERECE REENVIAR', () => {
    // Reenviar uma entregue mandaria o mesmo evento duas vezes para quem já processou; reenviar
    // uma pendente é redundante — ela já vai ser tentada sozinha.
    montar({
      webhook: webhook(),
      entregas: [
        entrega({ id: 1, status: 'entregue', podeReenviar: false }),
        entrega({ id: 2, status: 'pendente', podeReenviar: false, codigoResposta: null }),
        entrega({ id: 3, status: 'falhou', podeReenviar: true, tentativas: 3, codigoResposta: 500 })
      ]
    });

    expect(botoes('reenviar').length).toBe(1);
    expect(c.falhas()).toBe(1);

    c.reenviar(c.entregas()[2]);
    http.expectOne(r => r.url.endsWith('/webhooks-saida/entregas/3/reenviar') && r.method === 'POST')
      .flush(null);
    http.expectOne(r => r.method === 'GET').flush({ webhook: webhook(), entregas: [] });
  });

  it('o corpo da entrega abre indentado, e a tela diz que o assinado é o compacto', () => {
    montar({ webhook: webhook(), entregas: [entrega()] });

    c.alternarPayload(1);
    fixture.detectChanges();

    const t = texto();
    expect(t).toContain('"versao": 1');            // indentado para leitura
    expect(t).toContain('versão compacta');        // e o aviso de que o assinado é outro
  });

  // ==================================================================== a documentação
  it('O EXEMPLO DE VALIDAÇÃO ENSINA AS TRÊS COISAS QUE SE ERRA SOZINHO', () => {
    // ===== POR QUE ISTO É TESTE =====
    // Um snippet errado na tela vira receptor inseguro em TODO cliente que copiar. Os três erros
    // clássicos: assinar só o corpo (replay), reserializar o corpo (HMAC não bate), e comparar com
    // `===` (vaza a assinatura byte a byte pelo relógio).
    montar({ webhook: webhook(), entregas: [] });

    const exemplo = c.exemploNode();

    expect(exemplo).toContain("timestamp + '.' + corpo");
    expect(exemplo).toContain('express.raw');
    expect(exemplo).toContain('timingSafeEqual');
    expect(exemplo).toContain('X-Nexora-Assinatura');

    // E aparece na tela, não só no componente.
    expect(texto()).toContain('timingSafeEqual');
  });

  it('a tela documenta os quatro cabeçalhos', () => {
    montar({ webhook: webhook(), entregas: [] });

    const t = texto();
    for (const h of ['X-Nexora-Assinatura', 'X-Nexora-Timestamp', 'X-Nexora-Evento', 'X-Nexora-Entrega']) {
      expect(t).withContext(`${h} sumiu da documentação`).toContain(h);
    }

    // E o contrato de tempo: 10s de timeout, 3 tentativas, 30 dias de registro.
    expect(t).toContain('10 segundos');
    expect(t).toContain('30 dias');
  });

  // ==================================================================== largura
  it('é tela DENSA: `.pagina` sem o modificador de formulário', () => {
    montar({ webhook: webhook(), entregas: [entrega()] });

    const pagina = (fixture.nativeElement as HTMLElement).querySelector('.pagina')!;
    expect(pagina.classList.contains('formulario')).toBeFalse();
  });
});
