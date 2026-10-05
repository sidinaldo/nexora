import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ChaveOperador } from '../../nucleo/seguranca/chave-operador';
import { OperacaoEmpresas } from './empresas/empresas';
import { OperacaoPlanos } from './planos/planos';
import { Operacao } from './operacao';

/** ÁREA DO OPERADOR — a tela (OPE-1).
 *
 * ===================== O QUE ESTE ARQUIVO PROTEGE =====================
 *   1. sem chave, a área NÃO CHAMA A API. Pedir a chave e mesmo assim disparar requisições gastaria
 *      rate limit e encheria o log de 401 por nada;
 *   2. a chave vai no cabeçalho `X-Chave-Admin` e NÃO fica guardada no navegador;
 *   3. o 409 do excedente é um SEGUNDO PASSO, não um erro — e confirmar reenvia com a flag;
 *   4. as três frases que contrariam a leitura natural estão na tela: desativar não derruba quem
 *      já entrou, editar um plano não muda quem está nele, e o preço não cobra nada.
 * ====================================================================== */
describe('área do operador', () => {
  const CHAVE = 'chave-de-operacao-de-teste-0123456789';

  let http: HttpTestingController;
  let chave: ChaveOperador;

  function texto(f: ComponentFixture<unknown>): string {
    return (f.nativeElement as HTMLElement).textContent ?? '';
  }

  async function assentar(f: ComponentFixture<unknown>) {
    f.detectChanges();
    await f.whenStable();
    f.detectChanges();
  }

  /** Responde o que a lista e o catálogo pedem ao montar. */
  function responderMontagem(empresas: unknown[] = []) {
    http.match(r => r.url.includes('/operador/empresas'))
      .forEach(r => r.flush({ total: empresas.length, numeroPagina: 1, tamanho: 25, itens: empresas }));
    http.match(r => r.url.includes('/operador/planos')).forEach(r => r.flush([]));
  }

  function empresa(over: Record<string, unknown> = {}) {
    return {
      id: 7, nome: 'Padaria do Zé', ativa: true, demonstracao: false,
      planoId: null, planoNome: null, limiteConexoes: 1, limiteUsuarios: 3,
      limitesPersonalizados: false, criadaEm: '2026-09-01T10:00:00Z', horasAteValor: 12,
      usuariosAtivos: 3, usuariosConvidados: 0, vagasUsadas: 3,
      conexoes: 1, conexoesConectadas: 1, contatos: 40,
      ultimoAcessoEm: '2026-10-01T10:00:00Z', ultimaMensagemEm: '2026-10-01T09:00:00Z',
      negociacoesAbertas: 2, ganhasNaJanela: 5, valorGanhoNaJanela: 1200,
      ...over
    };
  }

  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();

    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });
    http = TestBed.inject(HttpTestingController);
    chave = TestBed.inject(ChaveOperador);
  });

  afterEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    TestBed.resetTestingModule();
  });

  // ==================================================================== a chave

  it('SEM CHAVE A ÁREA PEDE A CHAVE E NÃO CHAMA A API', async () => {
    // Disparar requisições de uma tela travada gastaria o rate limit e encheria o log de 401 por
    // nada — e o operador veria "não autorizado" antes de ter digitado qualquer coisa.
    const f = TestBed.createComponent(Operacao);
    await assentar(f);

    expect(texto(f)).toContain('chave de administração');
    http.expectNone(() => true);
  });

  it('A CHAVE VAI NO CABEÇALHO X-Chave-Admin', async () => {
    chave.definir(CHAVE);
    const f = TestBed.createComponent(OperacaoEmpresas);
    await assentar(f);

    const req = http.expectOne(r => r.url.includes('/operador/empresas'));
    expect(req.request.headers.get('X-Chave-Admin')).toBe(CHAVE);
    req.flush({ total: 0, numeroPagina: 1, tamanho: 25, itens: [] });
    http.match(() => true).forEach(r => r.flush([]));
  });

  it('A CHAVE NÃO FICA GUARDADA NO NAVEGADOR', async () => {
    // Afirmação bruta de propósito: sobrevive a uma refatoração que "ajude" lembrando a chave
    // entre recarregamentos. Recarregar pedir de novo é o comportamento desejado.
    chave.definir(CHAVE);
    const f = TestBed.createComponent(OperacaoEmpresas);
    await assentar(f);
    responderMontagem();

    expect(JSON.stringify(localStorage)).not.toContain(CHAVE);
    expect(JSON.stringify(sessionStorage)).not.toContain(CHAVE);
  });

  it('401 NA LISTA VOLTA A PEDIR A CHAVE', async () => {
    chave.definir(CHAVE);
    const f = TestBed.createComponent(OperacaoEmpresas);
    await assentar(f);

    http.expectOne(r => r.url.includes('/operador/empresas'))
      .flush({ erro: 'Não autorizado.' }, { status: 401, statusText: 'Unauthorized' });
    http.match(() => true).forEach(r => r.flush([]));
    await assentar(f);

    // Na lista não há nada preenchido para perder, então esquecer a chave é o que o operador faria
    // de qualquer jeito. (No editor é o contrário — ver o teste do 409.)
    expect(chave.temChave()).toBeFalse();
  });

  // ==================================================================== o excedente

  it('O EXCEDENTE É UM SEGUNDO PASSO, NÃO UM ERRO, E CONFIRMAR REENVIA COM A FLAG', async () => {
    chave.definir(CHAVE);
    const f = TestBed.createComponent(OperacaoEmpresas);
    const c = f.componentInstance;
    await assentar(f);
    responderMontagem([empresa()]);
    await assentar(f);

    c.abrir(empresa() as never);
    c.limiteUsuarios.set(1);
    c.salvarLimites();

    http.expectOne(r => r.url.includes('/limites')).flush(
      { erro: 'A empresa ficaria com 3 vagas ocupadas para um limite de 1. Ninguém perde acesso e '
            + 'nada é apagado — ela apenas não poderá incluir mais. Reenvie com confirmação para aplicar.' },
      { status: 409, statusText: 'Conflict' });
    await assentar(f);

    // A frase que impede o operador de achar que vai desligar gente.
    expect(texto(f)).toContain('Ninguém perde acesso');

    // ⚠️ E O BOTÃO DE CONFIRMAR, que é o que distingue "segundo passo" de "erro". Sem esta
    // asserção o teste passa com o 409 virando erro comum — a mensagem é a mesma nos dois casos,
    // e foi assim que ele passou por engano na primeira versão.
    const confirmar = [...(f.nativeElement as HTMLElement).querySelectorAll('button')]
      .filter(b => b.textContent?.includes('Aplicar mesmo assim'));
    expect(confirmar.length).withContext('o excedente tem de oferecer confirmar').toBe(1);
    // E a chave CONTINUA: 401 ou 409 no editor não podem custar o que está digitado.
    expect(chave.temChave()).toBeTrue();

    c.salvarLimites(true);
    const req = http.expectOne(r => r.url.includes('/limites'));
    expect((req.request.body as { confirmarExcedente: boolean }).confirmarExcedente).toBeTrue();
    req.flush({});
    http.match(() => true).forEach(r => r.flush({ total: 0, numeroPagina: 1, tamanho: 25, itens: [] }));
  });

  /** ===================== O GEMEO, E E ELE QUE PEGA O DEFEITO =====================
   *
   *  ⚠️ O BOTAO "APLICAR MESMO ASSIM" SERVE DUAS ACOES, e chamava sempre `salvarLimites(true)`.
   *  Quem escolhia um PLANO, levava o 409 e confirmava acabava salvando os LIMITES: a requisicao
   *  ia para `/limites`, `AjustarLimitesAsync` nao mexe em `plano_id` de proposito, a tela
   *  recarregava com SUCESSO e a coluna "Plano" continuava com um travessao. Nenhum erro, nenhuma
   *  pista — relatado como "plano nao esta sendo salvo".
   *
   *  ⚠️ O TESTE CLICA NO BOTAO, e nao chama `c.atribuirPlano(true)`. O defeito estava na LIGACAO
   *  do template; chamar o metodo passaria com o `(click)` apontando para o lugar errado, que e
   *  exatamente o que o teste de limites ao lado faz — e por isso ele nao pegou nada.
   *
   *  A URL e a assercao principal: `/plano` e nao `/limites`.
   *  ============================================================================== */
  it('CONFIRMAR O EXCEDENTE DE UM PLANO REENVIA O PLANO, nao os limites', async () => {
    chave.definir(CHAVE);
    const f = TestBed.createComponent(OperacaoEmpresas);
    const c = f.componentInstance;
    await assentar(f);

    http.match(r => r.url.includes('/operador/empresas'))
      .forEach(r => r.flush({ total: 1, numeroPagina: 1, tamanho: 25, itens: [empresa()] }));
    http.match(r => r.url.includes('/operador/planos')).forEach(r => r.flush([
      { id: 4, nome: 'Basico', limiteConexoes: 1, limiteUsuarios: 1, ativo: true }
    ]));
    await assentar(f);

    c.abrir(empresa() as never);
    c.planoEscolhido.set(4);
    c.atribuirPlano();

    http.expectOne(r => r.url.includes('/plano')).flush(
      { erro: 'A empresa ficaria com 3 vagas ocupadas para um limite de 1. Ninguem perde acesso e '
            + 'nada e apagado — ela apenas nao podera incluir mais. Reenvie com confirmação para aplicar.' },
      { status: 409, statusText: 'Conflict' });
    await assentar(f);

    const confirmar = [...(f.nativeElement as HTMLElement).querySelectorAll('button')]
      .filter(b => b.textContent?.includes('Aplicar mesmo assim'));
    expect(confirmar.length).withContext('o excedente tem de oferecer confirmar').toBe(1);

    confirmar[0].click();
    await assentar(f);

    // ⚠️ AQUI MORA O DEFEITO: antes do conserto esta requisicao saia para `/limites`.
    const req = http.expectOne(r => r.url.includes('/plano'));
    expect(req.request.url).withContext('confirmar um plano tem de reenviar o PLANO').toContain('/plano');
    expect((req.request.body as { planoId: number }).planoId).toBe(4);
    expect((req.request.body as { confirmarExcedente: boolean }).confirmarExcedente).toBeTrue();

    req.flush({});
    http.match(() => true).forEach(r => r.flush({ total: 0, numeroPagina: 1, tamanho: 25, itens: [] }));
  });

  /** O par: o caminho dos LIMITES continua indo para `/limites` depois do conserto. Sem ele, um
   *  "conserto" que mandasse tudo para `/plano` passaria. */
  it('CONFIRMAR O EXCEDENTE DE LIMITES CONTINUA REENVIANDO OS LIMITES', async () => {
    chave.definir(CHAVE);
    const f = TestBed.createComponent(OperacaoEmpresas);
    const c = f.componentInstance;
    await assentar(f);
    responderMontagem([empresa()]);
    await assentar(f);

    c.abrir(empresa() as never);
    c.limiteUsuarios.set(1);
    c.salvarLimites();

    http.expectOne(r => r.url.includes('/limites')).flush(
      { erro: 'A empresa ficaria com 3 vagas ocupadas para um limite de 1. Reenvie com confirmação para aplicar.' },
      { status: 409, statusText: 'Conflict' });
    await assentar(f);

    [...(f.nativeElement as HTMLElement).querySelectorAll('button')]
      .filter(b => b.textContent?.includes('Aplicar mesmo assim'))[0].click();
    await assentar(f);

    const req = http.expectOne(r => r.url.includes('/limites'));
    expect((req.request.body as { confirmarExcedente: boolean }).confirmarExcedente).toBeTrue();

    req.flush({});
    http.match(() => true).forEach(r => r.flush({ total: 0, numeroPagina: 1, tamanho: 25, itens: [] }));
  });

  // ==================================================================== as frases que importam

  it('A TELA DIZ QUE DESATIVAR NÃO DERRUBA QUEM JÁ ESTÁ DENTRO', async () => {
    // ⚠️ Um botão "desativar" que não desconecta ninguém é uma mentira que se descobre no meio de
    // um incidente. Enquanto o token de 12h não for reconferido, a frase É o aviso.
    chave.definir(CHAVE);
    const f = TestBed.createComponent(OperacaoEmpresas);
    await assentar(f);
    responderMontagem([empresa()]);
    await assentar(f);

    f.componentInstance.abrir(empresa() as never);
    await assentar(f);

    expect(texto(f)).toContain('bloqueia login novo');
    expect(texto(f)).toContain('12 horas');
  });

  it('O CATÁLOGO DIZ QUE EDITAR NÃO MUDA QUEM JÁ ESTÁ NO PLANO, E QUE O PREÇO NÃO COBRA', async () => {
    // As duas leituras naturais que a tela precisa contrariar: quem edita um plano acha que está
    // dando mais conexões a dez clientes (e não dá a nenhum), e quem muda um preço acha que mudou
    // o que o cliente paga (e não existe cobrança neste sistema).
    chave.definir(CHAVE);
    const f = TestBed.createComponent(OperacaoPlanos);
    await assentar(f);
    http.expectOne(r => r.url.includes('/operador/planos')).flush([]);
    await assentar(f);

    const t = texto(f);
    expect(t).toContain('não muda');
    expect(t).toContain('não cobra nada');
  });
});
