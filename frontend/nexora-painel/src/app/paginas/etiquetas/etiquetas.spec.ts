import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { EtiquetaDto } from '../../nucleo/modelos';
import { Etiquetas } from './etiquetas';

/** A TELA DE ETIQUETAS.
 *
 *  ===================== O QUE ESTES TESTES COBREM =====================
 *  O servidor é quem decide o que pode ser gravado; estes testes cobrem o que só a TELA faz —
 *  quando a mensagem de erro aparece, qual botão fica disponível, para onde o foco vai, e o que a
 *  lista mostra depois de filtrar.
 *
 *  Quase tudo aqui existe porque o DES-XX apontou um defeito concreto, e o nome de cada teste diz
 *  qual era.
 *  ==================================================================== */
describe('etiquetas', () => {
  const LISTA: EtiquetaDto[] = [
    { id: 1, nome: 'Revendedor', cor: '#2E7A56' },
    { id: 2, nome: 'Urgente', cor: '#B4552F' },
    { id: 3, nome: 'Ácido', cor: '#1D5B3F' }
  ];

  let componente: Etiquetas;
  let http: HttpTestingController;
  let fixture: ComponentFixture<Etiquetas>;

  function montar(lista: EtiquetaDto[] = LISTA) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    fixture = TestBed.createComponent(Etiquetas);
    componente = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);

    fixture.detectChanges();
    http.expectOne(r => r.url.includes('/etiquetas')).flush(lista);
    fixture.detectChanges();
    return fixture;
  }

  function raiz(): HTMLElement { return fixture.nativeElement as HTMLElement; }

  /** ⚠️ `ApplicationRef.tick()`, e nao `fixture.detectChanges()`.
   *
   *  Os dois renderizam, mas so o tick roda os ganchos de `afterNextRender` — que e onde vivem o
   *  foco inicial do formulario e a devolucao do foco ao cancelar. `detectChanges` sozinho
   *  atualiza o DOM e deixa os ganchos na fila, e o teste mediria um foco que nunca aconteceu. */
  async function renderizar() {
    fixture.detectChanges();
    TestBed.inject(ApplicationRef).tick();
    await fixture.whenStable();
  }
  function texto(): string { return raiz().textContent ?? ''; }

  afterEach(() => TestBed.resetTestingModule());

  // ==================================================================== validação
  it('O FORMULÁRIO LIMPO NÃO EXIBE ERRO NENHUM', async () => {
    // ===================== O DEFEITO QUE ORIGINOU A REGRA =====================
    // Abrir a tela e já ver "Dê um nome à etiqueta" é o sistema repreendendo alguém que ainda não
    // fez nada. Depois de duas telas assim o usuário para de ler as mensagens vermelhas —
    // inclusive as que importam.
    // =========================================================================
    montar();
    componente.abrirNovo();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(raiz().querySelector('app-etiqueta-form')).withContext('o formulário abriu').not.toBeNull();

    const erro = raiz().querySelector('.campo-erro');
    expect(erro?.textContent?.trim() || '')
      .withContext('nada foi digitado nem tocado — não há o que reclamar').toBe('');
  });

  it('CRIAR FICA DESABILITADO ATÉ O NOME SER PREENCHIDO', async () => {
    montar();
    componente.abrirNovo();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const botao = [...raiz().querySelectorAll('app-etiqueta-form button')]
      .find(b => b.textContent?.trim() === 'Criar') as HTMLButtonElement;

    expect(botao).withContext('o botão Criar existe').toBeTruthy();
    expect(botao.disabled).withContext('campo vazio').toBeTrue();
  });

  it('O ERRO APARECE DEPOIS DE TOCAR NO CAMPO E SAIR VAZIO', async () => {
    montar();
    componente.abrirNovo();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const campo = raiz().querySelector('app-etiqueta-form input:not([type=color])') as HTMLInputElement;
    campo.dispatchEvent(new Event('blur'));
    fixture.detectChanges();

    expect(raiz().querySelector('.campo-erro')?.textContent).toContain('Dê um nome');
  });

  // ==================================================================== erro do servidor
  it('A COLISÃO DE NOME APARECE ABAIXO DO CAMPO, NÃO NUMA NOTA DE RODAPÉ', async () => {
    // ===================== ONDE A MENSAGEM TEM DE ESTAR =====================
    // Havia um parágrafo fixo no rodapé explicando que "VIP" e "vip" são a mesma etiqueta. Ele
    // estava sempre lá, inclusive para quem nunca tentou repetir um nome — e sumia da vista
    // justamente de quem acabou de esbarrar na regra, que estava olhando o campo lá em cima.
    //
    // Agora a regra só fala quando é violada, e fala onde o erro aconteceu.
    // =======================================================================
    montar();
    componente.abrirNovo();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    componente.salvar({ nome: 'vip', cor: '#2E7A56' });
    http.expectOne(r => r.method === 'POST').flush(
      { erro: 'Já existe uma etiqueta chamada "vip".' },
      { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(raiz().querySelector('.campo-erro')?.textContent)
      .withContext('a mensagem do servidor entra no slot do campo').toContain('Já existe');

    expect(componente.editando())
      .withContext('o formulário continua aberto — fechar obrigaria a redigitar tudo').toBe('novo');

    expect(texto())
      .withContext('a nota fixa sobre maiúsculas saiu do rodapé').not.toContain('são a mesma etiqueta');
  });

  // ==================================================================== busca e ordem
  it('A BUSCA SÓ APARECE QUANDO A LISTA JUSTIFICA', () => {
    montar();
    expect(componente.mostrarBusca()).withContext('três etiquetas').toBeFalse();

    const muitas = Array.from({ length: 11 }, (_, i) => (
      { id: i + 1, nome: `Etiqueta ${i}`, cor: '#2E7A56' }));
    TestBed.resetTestingModule();
    montar(muitas);

    expect(componente.mostrarBusca()).withContext('onze etiquetas').toBeTrue();
  });

  it('A BUSCA FILTRA POR TRECHO, SEM OLHAR MAIÚSCULA', () => {
    montar();
    componente.busca.set('REVEND');
    expect(componente.visiveis().map(e => e.nome)).toEqual(['Revendedor']);
  });

  it('O CAMPO DE BUSCA NÃO SOME QUANDO O FILTRO REDUZ A LISTA', () => {
    // ⚠️ É por isso que a busca é em MEMÓRIA. Se o filtro fosse do servidor, `lista()` passaria a
    // ter os resultados filtrados, cairia abaixo do mínimo e o campo desapareceria no meio da
    // digitação — levando o texto junto.
    const muitas = Array.from({ length: 12 }, (_, i) => (
      { id: i + 1, nome: `Etiqueta ${i}`, cor: '#2E7A56' }));
    montar(muitas);

    componente.busca.set('Etiqueta 1');
    expect(componente.visiveis().length).toBeLessThan(muitas.length);
    expect(componente.mostrarBusca()).withContext('o total não mudou').toBeTrue();
  });

  it('A ORDEM POR NOME RESPEITA ACENTO', () => {
    // `'Ácido' < 'Revendedor'` é FALSE em comparação bruta: Á é U+00C1, acima de R. Numa lista
    // que o dono escreve em português, a primeira palavra acentuada denuncia.
    montar();
    componente.ordem.set('nome');
    expect(componente.visiveis().map(e => e.nome)).toEqual(['Ácido', 'Revendedor', 'Urgente']);
  });

  it('A ORDEM RECENTES VEM DA MAIS NOVA PARA A MAIS ANTIGA', () => {
    montar();
    componente.ordem.set('recentes');
    expect(componente.visiveis().map(e => e.id)).toEqual([3, 2, 1]);
  });

  // ==================================================================== estado vazio
  it('SEM NENHUMA ETIQUETA, AS SUGESTÕES CRIAM COM UM CLIQUE', () => {
    montar([]);

    const sugestoes = [...raiz().querySelectorAll('.chip-botao')] as HTMLButtonElement[];
    expect(sugestoes.length).withContext('cinco sementes').toBe(5);
    expect(sugestoes[0].textContent).toContain('Revendedor');

    sugestoes[0].click();

    const pedido = http.expectOne(r => r.method === 'POST');
    expect(pedido.request.body.nome).toBe('Revendedor');
    pedido.flush({ id: 9 });
  });

  // ==================================================================== exclusão
  it('APAGAR PERGUNTA ANTES, E O MODAL DIZ O QUE NÃO ACONTECE', () => {
    // ⚠️ "Nenhum contato é apagado" é a frase que importa. Sem ela o dono hesita, porque a
    // pergunta que a confirmação levanta é justamente essa.
    montar();
    componente.confirmarRemocao(LISTA[0]);
    fixture.detectChanges();

    const modal = raiz().querySelector('.overlay .modal');
    expect(modal).withContext('usa o contrato .overlay/.modal do design system').not.toBeNull();
    expect(modal?.getAttribute('role')).toBe('dialog');
    expect(modal?.textContent).toContain('Nenhum contato é apagado');

    http.expectNone(r => r.method === 'DELETE');
  });

  it('ESC FECHA O MODAL SEM APAGAR', () => {
    montar();
    componente.confirmarRemocao(LISTA[0]);
    fixture.detectChanges();

    componente.aoTeclarNoModal(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();

    expect(componente.removendo()).toBeNull();
    http.expectNone(r => r.method === 'DELETE');
  });

  it('O DUPLO CLIQUE NÃO APAGA DUAS VEZES', () => {
    // `salvando` é o que segura. Sem ele, dois cliques rápidos mandam dois DELETE e o segundo
    // volta "Etiqueta não encontrada" — erro assustador para quem só clicou com pressa.
    montar();
    componente.confirmarRemocao(LISTA[0]);
    componente.remover();
    componente.remover();

    http.expectOne(r => r.method === 'DELETE').flush({});
  });

  // ==================================================================== foco
  it('CANCELAR DEVOLVE O FOCO AO BOTÃO QUE ABRIU A EDIÇÃO', async () => {
    // ===================== POR QUE ISTO IMPORTA =====================
    // Sem devolver o foco, quem usa teclado cancela a edição e o foco cai no início do documento.
    // Numa lista de trinta etiquetas isso é percorrer a tela inteira de novo para chegar na linha
    // seguinte — e é o tipo de coisa que só aparece quando se testa sem mouse.
    // ===============================================================
    montar();

    const botao = [...raiz().querySelectorAll('button')]
      .find(b => b.getAttribute('aria-label') === 'Editar etiqueta Revendedor') as HTMLButtonElement;
    expect(botao).withContext('o aria-label traz o nome da etiqueta').toBeTruthy();

    document.body.appendChild(raiz());   // focar exige estar no documento
    botao.focus();
    botao.click();
    await renderizar();

    componente.fechar();
    await renderizar();

    // ⚠️ COMPARA O RÓTULO, NÃO A IDENTIDADE DO ELEMENTO — e a diferença é o defeito que este
    // teste encontrou. Abrir o formulário troca o ramo de um `@if`, e o Angular DESTRÓI o bloco:
    // o botão que volta é um elemento novo. A primeira versão guardava o `HTMLElement` clicado e
    // chamava `.focus()` nele depois; como ele já estava desconectado, não acontecia nada, em
    // silêncio. Por isso a tela guarda uma CHAVE (`data-foco`) e procura o botão de novo.
    expect(document.activeElement?.getAttribute('data-foco'))
      .withContext('o foco voltou para o botão de origem').toBe('editar-1');
    expect(document.activeElement).not.toBe(botao);
  });

  it('CADA BOTÃO DA LINHA TEM UM RÓTULO ÚNICO PARA LEITOR DE TELA', () => {
    // Três "Editar" idênticos numa lista de três não dizem qual é qual para quem ouve a tela.
    montar();

    const rotulos = [...raiz().querySelectorAll('.linha-acoes button')]
      .map(b => b.getAttribute('aria-label'));

    expect(rotulos).toEqual([
      'Editar etiqueta Ácido', 'Apagar etiqueta Ácido',
      'Editar etiqueta Revendedor', 'Apagar etiqueta Revendedor',
      'Editar etiqueta Urgente', 'Apagar etiqueta Urgente'
    ]);
    expect(new Set(rotulos).size).withContext('nenhum repetido').toBe(rotulos.length);
  });

  // ==================================================================== teto
  it('NO TETO DE 60 O BOTÃO DE CRIAR FICA INDISPONÍVEL, COM O MOTIVO NO TÍTULO', () => {
    const cheia = Array.from({ length: 60 }, (_, i) => (
      { id: i + 1, nome: `Etiqueta ${i}`, cor: '#2E7A56' }));
    montar(cheia);

    const botao = [...raiz().querySelectorAll('button')]
      .find(b => b.textContent?.includes('Nova etiqueta')) as HTMLButtonElement;

    expect(botao.disabled).toBeTrue();
    expect(botao.title).withContext('desabilitar sem dizer por quê é pior que não desabilitar')
      .toContain('Apague alguma');
  });
});
