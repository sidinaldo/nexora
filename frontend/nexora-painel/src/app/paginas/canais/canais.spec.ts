import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Canais as CanaisDto, CanalDto } from '../../nucleo/modelos';
import { Canais } from './canais';

/** CANAIS DE CAPTAÇÃO NA TELA (INT-2).
 *
 *  ===================== O QUE ESTE ARQUIVO PROTEGE =====================
 *  Quatro decisões que são fáceis de "melhorar" para pior:
 *
 *    1. quem decide se dá para APAGAR é o servidor (`podeRemover`/`motivoNaoRemove`) — só o banco
 *       sabe se já veio lead. Recalcular aqui daria um botão habilitado que devolve erro;
 *    2. sem número pareado NÃO se cria canal: o link sairia sem telefone e o cliente só
 *       descobriria depois da gráfica;
 *    3. o CÓDIGO não vai no corpo do PUT. Ele está impresso em papel que não volta;
 *    4. a tela diz que o contador é PISO. Apresentá-lo como total seria mentir sobre a única
 *       coisa que o cliente usa para decidir se o panfleto valeu a pena.
 *  ====================================================================== */
describe('canais — QR Code e links', () => {
  function canal(over: Partial<CanalDto> = {}): CanalDto {
    return {
      id: 1, nome: 'Balcão da loja', codigo: 'k7m2',
      conexaoId: 10, conexaoNome: 'Principal', numero: '5584988887777',
      origem: 'qrcode', ativo: true, leadsRecebidos: 0,
      link: 'https://wa.me/5584988887777?text=Ol%C3%A1%21%20Tenho%20interesse.%20%23k7m2',
      texto: 'Olá! Tenho interesse. #k7m2',
      nomeArquivo: 'nexora-balcao-da-loja-k7m2',
      podeRemover: true, motivoNaoRemove: null,
      criadoEm: '2026-08-01T10:00:00Z',
      ...over
    };
  }

  const CONEXAO = { id: 10, nome: 'Principal', numero: '5584988887777' };

  let http: HttpTestingController;
  let fixture: ComponentFixture<Canais>;
  let c: Canais;

  function montar(resposta: CanaisDto) {
    fixture = TestBed.createComponent(Canais);
    c = fixture.componentInstance;
    fixture.detectChanges();

    http.expectOne(r => r.url.endsWith('/canais') && r.method === 'GET').flush(resposta);
    fixture.detectChanges();
  }

  function texto(): string { return (fixture.nativeElement as HTMLElement).textContent ?? ''; }

  /** Os botões cujo texto começa com o rótulo. Comparação em minúsculas porque a lista virou
   *  TABELA no NAV-1 e as ações por linha usam `link-editar`, que é escrito em caixa baixa —
   *  amarrar o teste à caixa faria ele reprovar por uma decisão de estilo. */
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

  afterEach(() => { TestBed.resetTestingModule(); });

  // ==================================================================== apagar
  it('O BOTÃO APAGAR OBEDECE O SERVIDOR, NÃO UM CÁLCULO DA TELA', () => {
    montar({
      conexoes: [CONEXAO], podeCriar: true, leadsAtribuidos: 12,
      itens: [
        canal({ id: 1, nome: 'Panfleto Julho', leadsRecebidos: 12, podeRemover: false,
                motivoNaoRemove: 'Este canal já trouxe 12 leads. Desative em vez de apagar.' }),
        canal({ id: 2, nome: 'Vitrine', codigo: 'b3nx' })
      ]
    });

    const apagar = botoes('Apagar');
    expect(apagar.length).toBe(2);

    expect(apagar[0].disabled).withContext('o que já trouxe lead deveria estar travado').toBeTrue();
    expect(apagar[0].title).toBe('Este canal já trouxe 12 leads. Desative em vez de apagar.');
    expect(apagar[1].disabled).toBeFalse();
  });

  it('apagar passa pelo painel de confirmação e só então chama DELETE', () => {
    montar({ conexoes: [CONEXAO], podeCriar: true, leadsAtribuidos: 0,
             itens: [canal({ id: 7, nome: 'Teste' })] });

    c.pedirRemocao(c.lista()[0]);
    fixture.detectChanges();

    // A alternativa (desativar) precisa caber junto do aviso — `confirm()` do navegador não
    // comporta dois parágrafos.
    expect(texto()).toContain('Apagar "Teste"');
    expect(texto()).toContain('desative');
    http.expectNone(() => true);

    c.confirmarRemocao();
    http.expectOne(r => r.url.endsWith('/canais/7') && r.method === 'DELETE').flush(null);
    http.expectOne(r => r.url.endsWith('/canais') && r.method === 'GET')
      .flush({ conexoes: [CONEXAO], podeCriar: true, leadsAtribuidos: 0, itens: [] });
  });

  // ==================================================================== sem número
  it('SEM NÚMERO PAREADO A TELA NÃO OFERECE CRIAR, E DIZ POR QUÊ', () => {
    // O link embute o telefone. Sem número sairia `https://wa.me/?text=...` — um QR que escaneia,
    // abre o WhatsApp e não leva a lugar nenhum. Impresso em panfleto, é dinheiro jogado fora.
    montar({ conexoes: [], podeCriar: false, leadsAtribuidos: 0, itens: [] });

    expect(texto()).toContain('Nenhum número de WhatsApp está conectado');
    expect(botoes('Criar').length).toBe(0);
  });

  it('canal cujo número caiu aparece com aviso e não desenha QR', () => {
    montar({
      conexoes: [CONEXAO], podeCriar: true, leadsAtribuidos: 3,
      itens: [canal({ id: 5, numero: null, link: null, leadsRecebidos: 3 })]
    });

    expect(c.semNumero().length).toBe(1);
    expect(texto()).toContain('com o número desconectado');

    // Abrir NÃO pede o SVG: não há link para codificar.
    c.abrir(c.lista()[0]);
    http.expectNone(r => r.url.includes('qr.svg'));
    fixture.detectChanges();
    expect(texto()).toContain('não está pareado');
  });

  // ==================================================================== criar
  it('criar manda POST e abre o QR do canal novo', () => {
    montar({ conexoes: [CONEXAO], podeCriar: true, leadsAtribuidos: 0, itens: [] });

    c.fNome.set('Panfleto Julho');
    c.criar();

    const post = http.expectOne(r => r.url.endsWith('/canais') && r.method === 'POST');
    expect(post.request.body).toEqual({ nome: 'Panfleto Julho', conexaoId: 10, origem: 'qrcode' });
    post.flush({ id: 9 });

    http.expectOne(r => r.url.endsWith('/canais') && r.method === 'GET').flush({
      conexoes: [CONEXAO], podeCriar: true, leadsAtribuidos: 0,
      itens: [canal({ id: 9, nome: 'Panfleto Julho' })]
    });

    // Criar sem baixar o QR não serve para nada — o próximo passo é sempre o mesmo.
    expect(c.abertoId()).toBe(9);
    http.expectOne(r => r.url.endsWith('/canais/9/qr.svg'))
      .flush(new Blob(['<svg/>'], { type: 'image/svg+xml' }));
  });

  // ==================================================================== editar
  it('EDITAR MANDA NOME, CONEXÃO E ORIGEM — NUNCA O CÓDIGO', () => {
    // ===== O CÓDIGO JÁ ESTÁ IMPRESSO =====
    // Trocá-lo transformaria todo material distribuído em link sem atribuição: funcionando, mas
    // mudo. Não existe campo, não existe rota, e este teste garante que não passa por engano.
    montar({ conexoes: [CONEXAO], podeCriar: true, leadsAtribuidos: 0,
             itens: [canal({ id: 4, nome: 'Vitrine', codigo: 'b3nx' })] });

    c.editar(c.lista()[0]);
    c.eNome.set('Vitrine da frente');
    c.salvarEdicao(c.lista()[0]);

    const put = http.expectOne(r => r.url.endsWith('/canais/4') && r.method === 'PUT');
    expect(put.request.body).toEqual({ nome: 'Vitrine da frente', conexaoId: 10, origem: 'qrcode' });
    expect(JSON.stringify(put.request.body)).not.toContain('b3nx');
    expect(JSON.stringify(put.request.body)).not.toContain('codigo');
    put.flush(null);

    http.expectOne(r => r.url.endsWith('/canais') && r.method === 'GET')
      .flush({ conexoes: [CONEXAO], podeCriar: true, leadsAtribuidos: 0, itens: [] });
  });

  // ==================================================================== download
  it('baixar SVG e PNG passa pelo HttpClient — e não por um link direto', () => {
    // As rotas do painel exigem `Authorization: Bearer`. Um `<a href="/api/...">` navegaria sem
    // cabeçalho e abriria um 401 — o download tem que passar pelo interceptor.
    montar({ conexoes: [CONEXAO], podeCriar: true, leadsAtribuidos: 0, itens: [canal({ id: 3 })] });

    c.baixarSvg(c.lista()[0]);
    http.expectOne(r => r.url.endsWith('/canais/3/qr.svg') && r.method === 'GET')
      .flush(new Blob(['<svg/>'], { type: 'image/svg+xml' }));

    c.baixarPng(c.lista()[0]);
    http.expectOne(r => r.url.endsWith('/canais/3/qr.png') && r.method === 'GET')
      .flush(new Blob([new Uint8Array([137, 80, 78, 71])], { type: 'image/png' }));
  });

  // ==================================================================== o texto honesto
  it('A TELA MOSTRA O TEXTO DO LINK E DIZ QUE O CONTADOR É PISO', () => {
    // ===== O QUE NÃO PODE SUMIR DA TELA =====
    // O rastreio é frágil de propósito: a pessoa pode apagar o texto antes de mandar. Quem cria o
    // canal precisa VER a frase que o cliente dele vai enviar — é ela que decide se o código
    // sobrevive. E o número de leads é um piso, porque quem apagou o código não aparece.
    montar({ conexoes: [CONEXAO], podeCriar: true, leadsAtribuidos: 5,
             itens: [canal({ id: 2, leadsRecebidos: 5 })] });

    c.abrir(c.lista()[0]);
    http.expectOne(r => r.url.endsWith('/canais/2/qr.svg'))
      .flush(new Blob(['<svg/>'], { type: 'image/svg+xml' }));
    fixture.detectChanges();

    // O texto vive num `input readonly`, e valor de input NÃO entra em `textContent` — é por isso
    // que a asserção lê o campo, e não a página.
    const campoTexto = (fixture.nativeElement as HTMLElement)
      .querySelector('#texto') as HTMLInputElement;
    expect(campoTexto.value)
      .withContext('a frase que a pessoa vai enviar').toBe('Olá! Tenho interesse. #k7m2');

    const t = texto();
    expect(t).toContain('piso');
    expect(t).toContain('atribuição errada é pior que atribuição ausente');
  });

  it('o link é o do servidor, com o texto escapado — a tela não o remonta', () => {
    // Remontar o link aqui seria a segunda cópia de uma regra que já existe no servidor. E o `#`
    // não escapado é a falha silenciosa deste bloco: o WhatsApp receberia a frase truncada.
    montar({ conexoes: [CONEXAO], podeCriar: true, leadsAtribuidos: 0, itens: [canal({ id: 2 })] });

    c.abrir(c.lista()[0]);
    http.expectOne(r => r.url.endsWith('/canais/2/qr.svg'))
      .flush(new Blob(['<svg/>'], { type: 'image/svg+xml' }));
    fixture.detectChanges();

    const campo = (fixture.nativeElement as HTMLElement)
      .querySelector('#link') as HTMLInputElement;

    expect(campo.value).toBe(c.lista()[0].link!);
    expect(campo.value).toContain('%23k7m2');
    expect(campo.readOnly).withContext('o link não é editável na tela').toBeTrue();
  });
});
